# NOTE
# OpenCvSharp4.official.runtime.linux-x64 brings the OpenCvSharpExtern binary.
# That binary is built against Ubuntu 22.04 (Jammy) FFmpeg sonames (libavcodec.so.58, etc.).
# Using the Jammy-flavoured .NET images avoids the classic Debian 12 (Bookworm) mismatch
# (Bookworm ships libavcodec.so.59), which otherwise causes DllNotFoundException at runtime.

FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy AS base
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    libglib2.0-0 \
    libsm6 \
    libxrender1 \
    libxext6 \
    libfontconfig1 \
    libfreetype6 \
    libjpeg-turbo8 \
    libpng16-16 \
    libtiff5 \
    libwebp7 \
    libopenjp2-7 \
    libgtk2.0-0 \
    libdc1394-25 \
    libavcodec58 \
    libavformat58 \
    libavutil56 \
    libswscale5 \
    libswresample3 \
    tesseract-ocr \
    libtesseract4 \
    liblept5 \
  && rm -rf /var/lib/apt/lists/*


FROM mcr.microsoft.com/dotnet/sdk:9.0-jammy AS build
WORKDIR /src

COPY . .
WORKDIR /src
RUN dotnet restore "./taskforge.csproj"
RUN dotnet publish "./taskforge.csproj" -c Release -o /app/out --no-restore


FROM base AS final
WORKDIR /app
COPY --from=build /app/out .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "taskforge.dll"]
