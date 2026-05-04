# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TelegramQuizBot.csproj ./
RUN dotnet restore ./TelegramQuizBot.csproj

COPY . .
RUN dotnet publish ./TelegramQuizBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libgssapi-krb5-2 \
        tzdata \
        wget \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableAlternateStackCheck=1

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=5 \
    CMD wget -qO- http://localhost:8080/ready >/dev/null 2>&1 || exit 1

ENTRYPOINT ["dotnet", "TelegramQuizBot.dll"]
