FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY services/shared services/shared
COPY services/execution/worker/TaskForge.Execution.Worker.csproj services/execution/worker/
RUN dotnet restore services/execution/worker/TaskForge.Execution.Worker.csproj
COPY services/execution/worker services/execution/worker
RUN dotnet publish services/execution/worker/TaskForge.Execution.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=TaskForge.Execution.Worker\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Debug}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Information}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Information}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Information}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Information}\" dotnet TaskForge.Execution.Worker.dll; else echo \"[taskforge-debug] logs=off service=TaskForge.Execution.Worker\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Information}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Warning}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Warning}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Warning}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Warning}\" dotnet TaskForge.Execution.Worker.dll; fi"]
