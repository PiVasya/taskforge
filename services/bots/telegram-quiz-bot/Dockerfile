# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY services/shared services/shared
COPY services/bots/telegram-quiz-bot/TelegramQuizBot.csproj services/bots/telegram-quiz-bot/
RUN dotnet restore services/bots/telegram-quiz-bot/TelegramQuizBot.csproj
COPY services/bots/telegram-quiz-bot services/bots/telegram-quiz-bot
RUN dotnet publish services/bots/telegram-quiz-bot/TelegramQuizBot.csproj -c Release -o /app/publish /p:UseAppHost=false

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

ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=TelegramQuizBot\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Debug}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Information}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Information}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Information}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Information}\" dotnet TelegramQuizBot.dll; else echo \"[taskforge-debug] logs=off service=TelegramQuizBot\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Information}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Warning}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Warning}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Warning}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Warning}\" dotnet TelegramQuizBot.dll; fi"]
