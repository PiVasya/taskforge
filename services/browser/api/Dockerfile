# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/browser/api/TaskForge.Browser.Api.csproj services/browser/api/
RUN --mount=type=cache,id=taskforge-browser-nuget,target=/root/.nuget/packages \
    dotnet restore services/browser/api/TaskForge.Browser.Api.csproj
COPY services/browser/api/ services/browser/api/
RUN --mount=type=cache,id=taskforge-browser-nuget,target=/root/.nuget/packages \
    dotnet publish services/browser/api/TaskForge.Browser.Api.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained true \
      --no-restore \
      -o /app/publish \
      /p:DebugType=None \
      /p:DebugSymbols=false

# The Playwright image already contains a Chromium build matching Microsoft.Playwright 1.61.0
# and all native libraries required by that browser.
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble AS runtime
ARG TASKFORGE_DEBUG_LOGS=1
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    HOME=/tmp \
    TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
WORKDIR /app
COPY --from=build --chown=pwuser:pwuser /app/publish/ ./
USER pwuser
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=TaskForge.Browser.Api\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Debug}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Information}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Information}\" /app/TaskForge.Browser.Api; else echo \"[taskforge-debug] logs=off service=TaskForge.Browser.Api\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Information}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Warning}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Warning}\" /app/TaskForge.Browser.Api; fi"]
