FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY learning-content-service.csproj ./
RUN dotnet restore learning-content-service.csproj

COPY . ./
RUN dotnet publish learning-content-service.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "learning-content-service.dll"]
