FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
      wine wine64 xvfb curl ca-certificates git libicu74 xauth fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
RUN apt-get update && apt-get install -y --no-install-recommends x11-apps imagemagick procps fonts-wine fonts-liberation && rm -rf /var/lib/apt/lists/*
