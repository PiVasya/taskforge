# Runtime base
#
# Why Ubuntu 24.04?
#   Our image similarity service uses OpenCvSharp native bindings.
#   The repo uses an Ubuntu 24.04 native runtime package (see taskforge.csproj):
#     OpenCvSharp4.unofficial.runtime.ubuntu.24.04-x64
#   Using an Ubuntu 24.04 runtime image ensures all required system libraries
#   match (and avoids Debian/FFmpeg SONAME mismatches like libavcodec.so.58).

FROM ubuntu:24.04 AS base

ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /app

# OS + native deps for OpenCvSharp + (optional) Tesseract
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        apt-transport-https \
        libglib2.0-0 \
        libsm6 \
        libxrender1 \
        libxext6 \
        libfontconfig1 \
        libfreetype6 \
        libjpeg-turbo8 \
        libpng16-16 \
        libtiff6 \
        libwebp7 \
        libopenjp2-7 \
        libdc1394-25 \
        tesseract-ocr \
        libtesseract5 \
        liblept5 \
    && rm -rf /var/lib/apt/lists/*

# Install ASP.NET Core Runtime 9.0 from Microsoft's apt repo (Ubuntu 24.04).
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb; \
    dpkg -i /tmp/packages-microsoft-prod.deb; \
    rm /tmp/packages-microsoft-prod.deb; \
    apt-get update; \
    apt-get install -y --no-install-recommends aspnetcore-runtime-9.0; \
    rm -rf /var/lib/apt/lists/*


# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first for better caching
COPY ["taskforge.csproj", "./"]
RUN dotnet restore "./taskforge.csproj"

# Copy the rest and publish
COPY . .
RUN dotnet publish "./taskforge.csproj" -c Release -o /app/out --no-restore


# Final
FROM base AS final
WORKDIR /app
COPY --from=build /app/out ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENTRYPOINT ["dotnet", "taskforge.dll"]
