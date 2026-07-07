ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base:3.18
FROM $BUILD_FROM

# Install .NET 8 runtime (from Alpine community repo)
RUN apk add --no-cache \
        dotnet-aspnet8 \
        libgdiplus \
        opencv \
    && ln -s /usr/lib/libgdiplus.so /usr/lib/libgdiplus.so.0 2>/dev/null || true

WORKDIR /app

# Copy published app from host or CI build context
COPY . .

RUN chmod a+x /app/run.sh

ENTRYPOINT ["/app/run.sh"]