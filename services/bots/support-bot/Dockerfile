FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/bots/support-bot/TaskForge.SupportBot.csproj services/bots/support-bot/
RUN dotnet restore services/bots/support-bot/TaskForge.SupportBot.csproj
COPY services/bots/support-bot services/bots/support-bot
RUN dotnet publish services/bots/support-bot/TaskForge.SupportBot.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TaskForge.SupportBot.dll"]
