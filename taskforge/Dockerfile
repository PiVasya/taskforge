FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore -r linux-x64
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# OpenCvSharpExtern (from NuGet) is linked against libtesseract.so.4 on linux-x64.
# Debian bookworm provides libtesseract.so.5, so we install it and add a compatibility symlink.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libtesseract5 \
        liblept5 \
        libglib2.0-0 \
        libgl1 \
    && ln -sf /usr/lib/x86_64-linux-gnu/libtesseract.so.5 /usr/lib/x86_64-linux-gnu/libtesseract.so.4 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","taskforge.dll"]
