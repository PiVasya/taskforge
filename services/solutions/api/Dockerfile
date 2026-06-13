FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY services/shared services/shared
COPY services/solutions/api/TaskForge.Solutions.Api.csproj services/solutions/api/
RUN dotnet restore services/solutions/api/TaskForge.Solutions.Api.csproj
COPY services/solutions/api services/solutions/api
RUN dotnet publish services/solutions/api/TaskForge.Solutions.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=TaskForge.Solutions.Api\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Debug}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Information}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Information}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Information}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Information}\" dotnet TaskForge.Solutions.Api.dll; else echo \"[taskforge-debug] logs=off service=TaskForge.Solutions.Api\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Information}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Warning}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Warning}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Warning}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Warning}\" dotnet TaskForge.Solutions.Api.dll; fi"]
