using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Drawing
{
    /// <summary>
    /// Class ImageProcessor.
    /// </summary>
    public class ImageProcessor : IImageProcessor, IDisposable
    {
        // Increment this when there's a change requiring caches to be invalidated
        private const string Version = "3";

        private static readonly HashSet<string> _transparentImageTypes
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".webp", ".gif" };

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IServerApplicationPaths _appPaths;
        private IImageEncoder _imageEncoder;
        private readonly Func<ILibraryManager> _libraryManager;
        private readonly Func<IMediaEncoder> _mediaEncoder;

        private readonly Dictionary<string, LockInfo> _locks = new Dictionary<string, LockInfo>();
        private bool _disposed = false;

        /// <summary>
        ///
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="appPaths"></param>
        /// <param name="fileSystem"></param>
        /// <param name="imageEncoder"></param>
        /// <param name="libraryManager"></param>
        /// <param name="mediaEncoder"></param>
        public ImageProcessor(
            ILogger<ImageProcessor> logger,
            IServerApplicationPaths appPaths,
            IFileSystem fileSystem,
            IImageEncoder imageEncoder,
            Func<ILibraryManager> libraryManager,
            Func<IMediaEncoder> mediaEncoder)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _imageEncoder = imageEncoder;
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _appPaths = appPaths;

            ImageEnhancers = Array.Empty<IImageEnhancer>();

            ImageHelper.ImageProcessor = this;
        }

        private string ResizedImageCachePath => Path.Combine(_appPaths.ImageCachePath, "resized-images");

        private string EnhancedImageCachePath => Path.Combine(_appPaths.ImageCachePath, "enhanced-images");

        /// <inheritdoc />
        public IReadOnlyCollection<string> SupportedInputFormats =>
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "tiff",
                "tif",
                "jpeg",
                "jpg",
                "png",
                "aiff",
                "cr2",
                "crw",

                // Remove until supported
                //"nef",
                "orf",
                "pef",
                "arw",
                "webp",
                "gif",
                "bmp",
                "erf",
                "raf",
                "rw2",
                "nrw",
                "dng",
                "ico",
                "astc",
                "ktx",
                "pkm",
                "wbmp"
            };

        /// <inheritdoc />
        public IReadOnlyCollection<IImageEnhancer> ImageEnhancers { get; set; }

        /// <inheritdoc />
        public bool SupportsImageCollageCreation => _imageEncoder.SupportsImageCollageCreation;

        /// <inheritdoc />
        public IImageEncoder ImageEncoder
        {
            get => _imageEncoder;
            set => _imageEncoder = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <inheritdoc />
        public async Task ProcessImage(ImageProcessingOptions options, Stream toStream)
        {
            var file = await ProcessImage(options).ConfigureAwait(false);

            using (var fileStream = new FileStream(file.Item1, FileMode.Open, FileAccess.Read, FileShare.Read, IODefaults.FileStreamBufferSize, true))
            {
                await fileStream.CopyToAsync(toStream).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public IReadOnlyCollection<ImageFormat> GetSupportedImageOutputFormats()
            => _imageEncoder.SupportedOutputFormats;

        /// <inheritdoc />
        public bool SupportsTransparency(string path)
            => _transparentImageTypes.Contains(Path.GetExtension(path));

        /// <inheritdoc />
        public async Task<(string path, string mimeType, DateTime dateModified)> ProcessImage(ImageProcessingOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ItemImageInfo originalImage = options.Image;
            BaseItem item = options.Item;

            if (!originalImage.IsLocalFile)
            {
                if (item == null)
                {
                    item = _libraryManager().GetItemById(options.ItemId);
                }
                originalImage = await _libraryManager().ConvertImageToLocal(item, originalImage, options.ImageIndex).ConfigureAwait(false);
            }

            string originalImagePath = originalImage.Path;
            DateTime dateModified = originalImage.DateModified;
            ImageDimensions? originalImageSize = null;
            if (originalImage.Width > 0 && originalImage.Height > 0)
            {
                originalImageSize = new ImageDimensions(originalImage.Width, originalImage.Height);
            }

            if (!_imageEncoder.SupportsImageEncoding)
            {
                return (originalImagePath, MimeTypes.GetMimeType(originalImagePath), dateModified);
            }

            var supportedImageInfo = await GetSupportedImage(originalImagePath, dateModified).ConfigureAwait(false);
            originalImagePath = supportedImageInfo.path;

            if (!File.Exists(originalImagePath))
            {
                return (originalImagePath, MimeTypes.GetMimeType(originalImagePath), dateModified);
            }

            dateModified = supportedImageInfo.dateModified;
            bool requiresTransparency = _transparentImageTypes.Contains(Path.GetExtension(originalImagePath));

            if (options.Enhancers.Count > 0)
            {
                if (item == null)
                {
                    item = _libraryManager().GetItemById(options.ItemId);
                }

                var tuple = await GetEnhancedImage(new ItemImageInfo
                {
                    DateModified = dateModified,
                    Type = originalImage.Type,
                    Path = originalImagePath
                }, requiresTransparency, item, options.ImageIndex, options.Enhancers, CancellationToken.None).ConfigureAwait(false);

                originalImagePath = tuple.path;
                dateModified = tuple.dateModified;
                requiresTransparency = tuple.transparent;
                // TODO: Get this info
                originalImageSize = null;
            }

            bool autoOrient = false;
            ImageOrientation? orientation = null;
            if (item is Photo photo)
            {
                if (photo.Orientation.HasValue)
                {
                    if (photo.Orientation.Value != ImageOrientation.TopLeft)
                    {
                        autoOrient = true;
                        orientation = photo.Orientation;
                    }
                }
                else
                {
                    // Orientation unknown, so do it
                    autoOrient = true;
                    orientation = photo.Orientation;
                }
            }

            if (options.HasDefaultOptions(originalImagePath, originalImageSize) && (!autoOrient || !options.RequiresAutoOrientation))
            {
                // Just spit out the original file if all the options are default
                return (originalImagePath, MimeTypes.GetMimeType(originalImagePath), dateModified);
            }

            ImageDimensions newSize = ImageHelper.GetNewImageSize(options, null);
            int quality = options.Quality;

            ImageFormat outputFormat = GetOutputFormat(options.SupportedOutputFormats, requiresTransparency);
            string cacheFilePath = GetCacheFilePath(originalImagePath, newSize, quality, dateModified, outputFormat, options.AddPlayedIndicator, options.PercentPlayed, options.UnplayedCount, options.Blur, options.BackgroundColor, options.ForegroundLayer);

            CheckDisposed();

            LockInfo lockInfo = GetLock(cacheFilePath);

            await lockInfo.Lock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (!File.Exists(cacheFilePath))
                {
                    if (options.CropWhiteSpace && !SupportsTransparency(originalImagePath))
                    {
                        options.CropWhiteSpace = false;
                    }

                    string resultPath = _imageEncoder.EncodeImage(originalImagePath, dateModified, cacheFilePath, autoOrient, orientation, quality, options, outputFormat);

                    if (string.Equals(resultPath, originalImagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return (originalImagePath, MimeTypes.GetMimeType(originalImagePath), dateModified);
                    }
                }

                return (cacheFilePath, GetMimeType(outputFormat, cacheFilePath), _fileSystem.GetLastWriteTimeUtc(cacheFilePath));
            }
            catch (Exception ex)
            {
                // If it fails for whatever reason, return the original image
                _logger.LogError(ex, "Error encoding image");
                return (originalImagePath, MimeTypes.GetMimeType(originalImagePath), dateModified);
            }
            finally
            {
                ReleaseLock(cacheFilePath, lockInfo);
            }
        }

        private ImageFormat GetOutputFormat(IReadOnlyCollection<ImageFormat> clientSupportedFormats, bool requiresTransparency)
        {
            var serverFormats = GetSupportedImageOutputFormats();

            // Client doesn't care about format, so start with webp if supported
            if (serverFormats.Contains(ImageFormat.Webp) && clientSupportedFormats.Contains(ImageFormat.Webp))
            {
                return ImageFormat.Webp;
            }

            // If transparency is needed and webp isn't supported, than png is the only option
            if (requiresTransparency && clientSupportedFormats.Contains(ImageFormat.Png))
            {
                return ImageFormat.Png;
            }

            foreach (var format in clientSupportedFormats)
            {
                if (serverFormats.Contains(format))
                {
                    return format;
                }
            }

            // We should never actually get here
            return ImageFormat.Jpg;
        }

        private string GetMimeType(ImageFormat format, string path)
        {
            switch(format)
            {
                case ImageFormat.Bmp:  return MimeTypes.GetMimeType("i.bmp");
                case ImageFormat.Gif:  return MimeTypes.GetMimeType("i.gif");
                case ImageFormat.Jpg:  return MimeTypes.GetMimeType("i.jpg");
                case ImageFormat.Png:  return MimeTypes.GetMimeType("i.png");
                case ImageFormat.Webp: return MimeTypes.GetMimeType("i.webp");
                default:               return MimeTypes.GetMimeType(path);
            }
        }

        /// <summary>
        /// Gets the cache file path based on a set of parameters
        /// </summary>
        private string GetCacheFilePath(string originalPath, ImageDimensions outputSize, int quality, DateTime dateModified, ImageFormat format, bool addPlayedIndicator, double percentPlayed, int? unwatchedCount, int? blur, string backgroundColor, string foregroundLayer)
        {
            var filename = originalPath
                + "width=" + outputSize.Width
                + "height=" + outputSize.Height
                + "quality=" + quality
                + "datemodified=" + dateModified.Ticks
                + "f=" + format;

            if (addPlayedIndicator)
            {
                filename += "pl=true";
            }

            if (percentPlayed > 0)
            {
                filename += "p=" + percentPlayed;
            }

            if (unwatchedCount.HasValue)
            {
                filename += "p=" + unwatchedCount.Value;
            }

            if (blur.HasValue)
            {
                filename += "blur=" + blur.Value;
            }

            if (!string.IsNullOrEmpty(backgroundColor))
            {
                filename += "b=" + backgroundColor;
            }

            if (!string.IsNullOrEmpty(foregroundLayer))
            {
                filename += "fl=" + foregroundLayer;
            }

            filename += "v=" + Version;

            return GetCachePath(ResizedImageCachePath, filename, "." + format.ToString().ToLowerInvariant());
        }

        /// <inheritdoc />
        public ImageDimensions GetImageDimensions(BaseItem item, ItemImageInfo info)
            => GetImageDimensions(item, info, true);

        /// <inheritdoc />
        public ImageDimensions GetImageDimensions(BaseItem item, ItemImageInfo info, bool updateItem)
        {
            int width = info.Width;
            int height = info.Height;

            if (height > 0 && width > 0)
            {
                return new ImageDimensions(width, height);
            }

            string path = info.Path;
            _logger.LogInformation("Getting image size for item {ItemType} {Path}", item.GetType().Name, path);

            ImageDimensions size = GetImageDimensions(path);
            info.Width = size.Width;
            info.Height = size.Height;

            if (updateItem)
            {
                _libraryManager().UpdateImages(item);
            }

            return size;
        }

        /// <inheritdoc />
        public ImageDimensions GetImageDimensions(string path)
            => _imageEncoder.GetImageSize(path);

        /// <inheritdoc />
        public string GetImageCacheTag(BaseItem item, ItemImageInfo image)
        {
            var supportedEnhancers = GetSupportedEnhancers(item, image.Type).ToArray();

            return GetImageCacheTag(item, image, supportedEnhancers);
        }

        /// <inheritdoc />
        public string GetImageCacheTag(BaseItem item, ChapterInfo chapter)
        {
            try
            {
                return GetImageCacheTag(item, new ItemImageInfo
                {
                    Path = chapter.ImagePath,
                    Type = ImageType.Chapter,
                    DateModified = chapter.ImageDateModified
                });
            }
            catch
            {
                return null;
            }
        }

        /// <inheritdoc />
        public string GetImageCacheTag(BaseItem item, ItemImageInfo image, IReadOnlyCollection<IImageEnhancer> imageEnhancers)
        {
            string originalImagePath = image.Path;
            DateTime dateModified = image.DateModified;
            ImageType imageType = image.Type;

            // Optimization
            if (imageEnhancers.Count == 0)
            {
                return (originalImagePath + dateModified.Ticks).GetMD5().ToString("N", CultureInfo.InvariantCulture);
            }

            // Cache name is created with supported enhancers combined with the last config change so we pick up new config changes
            var cacheKeys = imageEnhancers.Select(i => i.GetConfigurationCacheKey(item, imageType)).ToList();
            cacheKeys.Add(originalImagePath + dateModified.Ticks);

            return string.Join("|", cacheKeys).GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }

        private async Task<(string path, DateTime dateModified)> GetSupportedImage(string originalImagePath, DateTime dateModified)
        {
            var inputFormat = Path.GetExtension(originalImagePath)
                .TrimStart('.')
                .Replace("jpeg", "jpg", StringComparison.OrdinalIgnoreCase);

            // These are just jpg files renamed as tbn
            if (string.Equals(inputFormat, "tbn", StringComparison.OrdinalIgnoreCase))
            {
                return (originalImagePath, dateModified);
            }

            if (!_imageEncoder.SupportedInputFormats.Contains(inputFormat))
            {
                try
                {
                    string filename = (originalImagePath + dateModified.Ticks.ToString(CultureInfo.InvariantCulture)).GetMD5().ToString("N", CultureInfo.InvariantCulture);

                    string cacheExtension = _mediaEncoder().SupportsEncoder("libwebp") ? ".webp" : ".png";
                    var outputPath = Path.Combine(_appPaths.ImageCachePath, "converted-images", filename + cacheExtension);

                    var file = _fileSystem.GetFileInfo(outputPath);
                    if (!file.Exists)
                    {
                        await _mediaEncoder().ConvertImage(originalImagePath, outputPath).ConfigureAwait(false);
                        dateModified = _fileSystem.GetLastWriteTimeUtc(outputPath);
                    }
                    else
                    {
                        dateModified = file.LastWriteTimeUtc;
                    }

                    originalImagePath = outputPath;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Image conversion failed for {Path}", originalImagePath);
                }
            }

            return (originalImagePath, dateModified);
        }

        /// <inheritdoc />
        public async Task<string> GetEnhancedImage(BaseItem item, ImageType imageType, int imageIndex)
        {
            var enhancers = GetSupportedEnhancers(item, imageType).ToArray();

            ItemImageInfo imageInfo = item.GetImageInfo(imageType, imageIndex);

            bool inputImageSupportsTransparency = SupportsTransparency(imageInfo.Path);

            var result = await GetEnhancedImage(imageInfo, inputImageSupportsTransparency, item, imageIndex, enhancers, CancellationToken.None);

            return result.path;
        }

        private async Task<(string path, DateTime dateModified, bool transparent)> GetEnhancedImage(
            ItemImageInfo image,
            bool inputImageSupportsTransparency,
            BaseItem item,
            int imageIndex,
            IReadOnlyCollection<IImageEnhancer> enhancers,
            CancellationToken cancellationToken)
        {
            var originalImagePath = image.Path;
            var dateModified = image.DateModified;
            var imageType = image.Type;

            try
            {
                var cacheGuid = GetImageCacheTag(item, image, enhancers);

                // Enhance if we have enhancers
                var enhancedImageInfo = await GetEnhancedImageInternal(originalImagePath, item, imageType, imageIndex, enhancers, cacheGuid, cancellationToken).ConfigureAwait(false);

                string enhancedImagePath = enhancedImageInfo.path;

                // If the path changed update dateModified
                if (!string.Equals(enhancedImagePath, originalImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    var treatmentRequiresTransparency = enhancedImageInfo.transparent;

                    return (enhancedImagePath, _fileSystem.GetLastWriteTimeUtc(enhancedImagePath), treatmentRequiresTransparency);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enhancing image");
            }

            return (originalImagePath, dateModified, inputImageSupportsTransparency);
        }

        /// <summary>
        /// Gets the enhanced image internal.
        /// </summary>
        /// <param name="originalImagePath">The original image path.</param>
        /// <param name="item">The item.</param>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <param name="supportedEnhancers">The supported enhancers.</param>
        /// <param name="cacheGuid">The cache unique identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task&lt;System.String&gt;.</returns>
        /// <exception cref="ArgumentNullException">
        /// originalImagePath
        /// or
        /// item
        /// </exception>
        private async Task<(string path, bool transparent)> GetEnhancedImageInternal(
            string originalImagePath,
            BaseItem item,
            ImageType imageType,
            int imageIndex,
            IReadOnlyCollection<IImageEnhancer> supportedEnhancers,
            string cacheGuid,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(originalImagePath))
            {
                throw new ArgumentNullException(nameof(originalImagePath));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var treatmentRequiresTransparency = false;
            foreach (var enhancer in supportedEnhancers)
            {
                if (!treatmentRequiresTransparency)
                {
                    treatmentRequiresTransparency = enhancer.GetEnhancedImageInfo(item, originalImagePath, imageType, imageIndex).RequiresTransparency;
                }
            }

            // All enhanced images are saved as png to allow transparency
            string cacheExtension = _imageEncoder.SupportedOutputFormats.Contains(ImageFormat.Webp) ?
                ".webp" :
                (treatmentRequiresTransparency ? ".png" : ".jpg");

            string enhancedImagePath = GetCachePath(EnhancedImageCachePath, cacheGuid + cacheExtension);

            LockInfo lockInfo = GetLock(enhancedImagePath);

            await lockInfo.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Check again in case of contention
                if (File.Exists(enhancedImagePath))
                {
                    return (enhancedImagePath, treatmentRequiresTransparency);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(enhancedImagePath));

                await ExecuteImageEnhancers(supportedEnhancers, originalImagePath, enhancedImagePath, item, imageType, imageIndex).ConfigureAwait(false);

                return (enhancedImagePath, treatmentRequiresTransparency);
            }
            finally
            {
                ReleaseLock(enhancedImagePath, lockInfo);
            }
        }

        /// <summary>
        /// Executes the image enhancers.
        /// </summary>
        /// <param name="imageEnhancers">The image enhancers.</param>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="item">The item.</param>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns>Task{EnhancedImage}.</returns>
        private static async Task ExecuteImageEnhancers(IEnumerable<IImageEnhancer> imageEnhancers, string inputPath, string outputPath, BaseItem item, ImageType imageType, int imageIndex)
        {
            // Run the enhancers sequentially in order of priority
            foreach (var enhancer in imageEnhancers)
            {
                await enhancer.EnhanceImageAsync(item, inputPath, outputPath, imageType, imageIndex).ConfigureAwait(false);

                // Feed the output into the next enhancer as input
                inputPath = outputPath;
            }
        }

        /// <summary>
        /// Gets the cache path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="uniqueName">Name of the unique.</param>
        /// <param name="fileExtension">The file extension.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="ArgumentNullException">
        /// path
        /// or
        /// uniqueName
        /// or
        /// fileExtension
        /// </exception>
        public string GetCachePath(string path, string uniqueName, string fileExtension)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (string.IsNullOrEmpty(uniqueName))
            {
                throw new ArgumentNullException(nameof(uniqueName));
            }

            if (string.IsNullOrEmpty(fileExtension))
            {
                throw new ArgumentNullException(nameof(fileExtension));
            }

            var filename = uniqueName.GetMD5() + fileExtension;

            return GetCachePath(path, filename);
        }

        /// <summary>
        /// Gets the cache path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="filename">The filename.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="ArgumentNullException">
        /// path
        /// or
        /// filename
        /// </exception>
        public string GetCachePath(string path, string filename)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (string.IsNullOrEmpty(filename))
            {
                throw new ArgumentNullException(nameof(filename));
            }

            var prefix = filename.Substring(0, 1);

            return Path.Combine(path, prefix, filename);
        }

        /// <inheritdoc />
        public void CreateImageCollage(ImageCollageOptions options)
        {
            _logger.LogInformation("Creating image collage and saving to {Path}", options.OutputPath);

            _imageEncoder.CreateImageCollage(options);

            _logger.LogInformation("Completed creation of image collage and saved to {Path}", options.OutputPath);
        }

        /// <inheritdoc />
        public IEnumerable<IImageEnhancer> GetSupportedEnhancers(BaseItem item, ImageType imageType)
        {
            foreach (var i in ImageEnhancers)
            {
                if (i.Supports(item, imageType))
                {
                    yield return i;
                }
            }
        }


        private class LockInfo
        {
            public SemaphoreSlim Lock = new SemaphoreSlim(1, 1);
            public int Count = 1;
        }

        private LockInfo GetLock(string key)
        {
            lock (_locks)
            {
                if (_locks.TryGetValue(key, out LockInfo info))
                {
                    info.Count++;
                }
                else
                {
                    info = new LockInfo();
                    _locks[key] = info;
                }
                return info;
            }
        }

        private void ReleaseLock(string key, LockInfo info)
        {
            info.Lock.Release();

            lock (_locks)
            {
                info.Count--;
                if (info.Count <= 0)
                {
                    _locks.Remove(key);
                    info.Lock.Dispose();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _disposed = true;

            var disposable = _imageEncoder as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
