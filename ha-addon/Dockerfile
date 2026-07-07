# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SourdoughMonitor.csproj .
COPY Directory.Build.props .
RUN dotnet restore -r linux-x64
COPY . .
RUN dotnet publish -c Release -r linux-x64 --self-contained true -o /app

# Runtime stage: Ubuntu 22.04 (jammy) - matches the ABI of the official
# OpenCvSharp native library (glibc 2.35, ffmpeg 4.4, tesseract 4, OpenEXR 2.5).
FROM ubuntu:22.04
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libtesseract4 libgtk2.0-0 libdc1394-25 \
        libavcodec58 libavformat58 libswscale5 \
        libjpeg-turbo8 libtiff5 libopenjp2-7 libopenexr25 \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

RUN missing=$(ldd /app/libOpenCvSharpExtern.so | grep "not found" || true); \
    if [ -n "$missing" ]; then \
        echo "ERROR: unresolved native dependencies:" >&2; \
        echo "$missing" >&2; \
        exit 1; \
    fi

ENTRYPOINT ["/app/SourdoughMonitor"]