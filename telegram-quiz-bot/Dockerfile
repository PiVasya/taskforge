# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TelegramQuizBot.csproj ./
RUN dotnet restore ./TelegramQuizBot.csproj

COPY . .
RUN dotnet publish ./TelegramQuizBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableAlternateStackCheck=1

EXPOSE 8080
ENTRYPOINT ["dotnet", "TelegramQuizBot.dll"]
