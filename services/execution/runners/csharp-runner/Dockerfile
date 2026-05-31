FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER 65534
ENTRYPOINT ["dotnet","csharp-runner.dll"]
