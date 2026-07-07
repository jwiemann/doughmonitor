ARG BUILD_FROM
FROM $BUILD_FROM

# Install .NET 8 runtime
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