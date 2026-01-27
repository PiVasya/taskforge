# Use the plain 9.0 SDK image instead of the jammy-specific tag.
# The `jammy` variant was not available on Docker Hub and caused the build
# to fail. The generic `9.0` tag points to a supported Debian-based image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore
# IMPORTANT: publish with a RuntimeIdentifier so native assets from NuGet (e.g. OpenCvSharpExtern.so)
# are copied to the output.
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

# Use the plain 9.0 ASP.NET runtime image rather than `9.0-jammy`. See comment above.
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# OpenCvSharpExtern from NuGet is linked against libtesseract.so.4 on linux-x64.
# Install runtime deps inside the container.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libtesseract4 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","taskforge.dll"]
