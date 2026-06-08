FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/solutions/rating-worker/TaskForge.Solutions.RatingWorker.csproj services/solutions/rating-worker/
RUN dotnet restore services/solutions/rating-worker/TaskForge.Solutions.RatingWorker.csproj
COPY services/solutions/rating-worker services/solutions/rating-worker
RUN dotnet publish services/solutions/rating-worker/TaskForge.Solutions.RatingWorker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TaskForge.Solutions.RatingWorker.dll"]
