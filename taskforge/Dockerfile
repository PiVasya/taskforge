FROM mcr.microsoft.com/dotnet/sdk:9.0-jammy AS build
WORKDIR /src
COPY . ./
RUN dotnet restore
# IMPORTANT: publish with a RuntimeIdentifier so native assets from NuGet (e.g. OpenCvSharpExtern.so)
# are copied to the output.
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy
WORKDIR /app

# OpenCvSharpExtern + OpenCV libs из NuGet требуют системные зависимости.
# Минимум: libtesseract + базовые X/GL/GLib либы, которые в aspnet-образе часто отсутствуют.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libtesseract4 \
        liblept5 \
        libglib2.0-0 \
        libsm6 \
        libxrender1 \
        libxext6 \
        libgl1 \
    && rm -rf /var/lib/apt/lists/*

# Чтобы загрузчик находил зависимости OpenCV, лежащие рядом с OpenCvSharpExtern.so.
ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64/native:/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH}

COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","taskforge.dll"]
