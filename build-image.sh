#!/usr/bin/env bash


VERSION=1.0

echo "BUILDING version ${VERSION}"

docker build -t jelly-custom .
docker tag jelly-custom "192.168.1.132:32000/jelly-custom:${VERSION}"
docker push "192.168.1.132:32000/jelly-custom:${VERSION}"

echo "Done building version ${VERSION}"
