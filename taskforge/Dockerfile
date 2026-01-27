FROM mcr.microsoft.com/dotnet/sdk:9.0-jammy AS build
WORKDIR /src
COPY . ./
RUN dotnet restore
# IMPORTANT: publish with a RuntimeIdentifier so native assets from NuGet (e.g. OpenCvSharpExtern.so)
# are copied to the output.
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy
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
