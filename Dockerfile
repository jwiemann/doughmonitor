FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ha-addon/SourdoughMonitor.csproj .
COPY ha-addon/Directory.Build.props .
RUN dotnet restore
COPY ha-addon/ .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdiplus libgtk-3-0 libtesseract5 libdc1394-25 libavcodec59 libavformat59 libswscale6 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SourdoughMonitor.dll"]