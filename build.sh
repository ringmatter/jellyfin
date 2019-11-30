#!/bin/bash

docker build -t jelly-custom2 .
docker tag jelly-custom2 192.168.1.9:5000/jelly-custom2
docker push 192.168.1.9:5000/jelly-custom2
