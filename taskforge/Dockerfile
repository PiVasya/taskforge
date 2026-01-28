# NOTE
# We intentionally use the Debian-based .NET images here.
# OpenCvSharp native bits are provided by Sdcb.OpenCvSharp4.mini.runtime.debian.12-x64
# (see taskforge.csproj), which matches Debian 12/Bookworm library SONAMEs
# (libavcodec59, libavformat59, etc.).

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

# Runtime deps for OpenCvSharp (Debian 12), image/OCR tooling, and common headless rendering.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    libglib2.0-0 \
    libsm6 \
    libxrender1 \
    libxext6 \
    libfontconfig1 \
    libfreetype6 \
    libjpeg62-turbo \
    libpng16-16 \
    libtiff6 \
    libwebp7 \
    libopenjp2-7 \
    libgtk2.0-0 \
    libdc1394-25 \
    libavcodec59 \
    libavformat59 \
    libavutil57 \
    libswscale6 \
    libswresample4 \
    tesseract-ocr \
    libtesseract5 \
    liblept5 \
  && rm -rf /var/lib/apt/lists/*

# Some libs look for older sonames. Provide compatibility symlink when needed.
RUN if [ -f /usr/lib/x86_64-linux-gnu/libtesseract.so.5 ] && [ ! -f /usr/lib/x86_64-linux-gnu/libtesseract.so.4 ]; then \
      ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so.5 /usr/lib/x86_64-linux-gnu/libtesseract.so.4; \
    fi


FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# NOTE: GitHub Actions builds this image with `context: ./taskforge`
# so the csproj lives at `/src/taskforge.csproj` (NOT `/src/taskforge/taskforge.csproj`).
#
# Copy csproj first for better layer caching.
COPY ./*.csproj ./
RUN dotnet restore "./taskforge.csproj"

# Copy the rest of the source.
COPY . ./
RUN dotnet publish "./taskforge.csproj" -c Release -o /app/out --no-restore


FROM base AS final
WORKDIR /app
COPY --from=build /app/out .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "taskforge.dll"]
