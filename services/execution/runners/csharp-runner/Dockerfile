FROM gcc:14-bookworm AS sandbox
WORKDIR /sandbox
COPY security/sandbox_preload.c ./
RUN mkdir -p /out \
    && gcc -shared -fPIC -O2 -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
    -Wl,-z,relro,-z,now,-z,noexecstack \
    -o /out/libtaskforge_sandbox.so sandbox_preload.c

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates util-linux \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY --from=sandbox /out/libtaskforge_sandbox.so /app/libtaskforge_sandbox.so
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080 9090
USER 65534
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=csharp-runner\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Debug}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Information}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Information}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Information}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Information}\" dotnet csharp-runner.dll; else echo \"[taskforge-debug] logs=off service=csharp-runner\"; exec env Logging__LogLevel__Default=\"${Logging__LogLevel__Default:-Information}\" Logging__LogLevel__Microsoft=\"${Logging__LogLevel__Microsoft:-Warning}\" Logging__LogLevel__Microsoft.AspNetCore=\"${Logging__LogLevel__Microsoft_AspNetCore:-Warning}\" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=\"${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Warning}\" Logging__LogLevel__System.Net.Http.HttpClient=\"${Logging__LogLevel__System_Net_Http_HttpClient:-Warning}\" dotnet csharp-runner.dll; fi"]
