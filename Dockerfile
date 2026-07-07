# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ha-addon/SourdoughMonitor.csproj .
COPY ha-addon/Directory.Build.props .
RUN dotnet restore
COPY ha-addon/ .
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgtk-3-0 libtesseract5 libdc1394-25 \
        libavcodec59 libavformat59 libswscale6 libfontconfig1 \
    && ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so.5 \
             /usr/lib/x86_64-linux-gnu/libtesseract.so.4 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV LD_LIBRARY_PATH="/app/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# Verify all native dependencies of OpenCvSharpExtern are resolvable.
# Fails the build and prints the missing libs if anything is unresolved.
RUN missing=$(ldd /app/runtimes/linux-x64/native/libOpenCvSharpExtern.so | grep "not found" || true); \
    if [ -n "$missing" ]; then \
        echo "ERROR: unresolved native dependencies:" >&2; \
        echo "$missing" >&2; \
        exit 1; \
    fi

ENTRYPOINT ["dotnet", "SourdoughMonitor.dll"]