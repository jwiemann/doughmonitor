ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base-debian:bookworm

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SourdoughMonitor.csproj .
COPY Directory.Build.props .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM $BUILD_FROM
RUN apt-get update && apt-get install -y --no-install-recommends \
        wget ca-certificates \
    && wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb \
    && apt-get update && apt-get install -y --no-install-recommends \
        dotnet-runtime-10.0 \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
COPY run.sh /app/run.sh
RUN chmod a+x /app/run.sh \
    && mkdir -p /etc/services.d/sourdough \
    && ln -s /app/run.sh /etc/services.d/sourdough/run