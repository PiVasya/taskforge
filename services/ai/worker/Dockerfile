FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY TaskForge.AiAgent.csproj ./
RUN dotnet restore ./TaskForge.AiAgent.csproj
COPY . ./
RUN dotnet publish ./TaskForge.AiAgent.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENTRYPOINT ["dotnet", "TaskForge.AiAgent.dll"]
