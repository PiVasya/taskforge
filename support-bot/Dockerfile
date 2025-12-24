# syntax=docker/dockerfile:1

# Multi-stage Dockerfile for the support-bot service.
#
# This file builds and publishes the support-bot project in a minimal
# runtime image. The resulting container will run the bot, which
# listens to incoming Telegram messages via long polling and writes
# replies back into the TaskForge database.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the entire repository into the build context. This Dockerfile
# assumes that the build context is the root of the repo so that both
# the taskforge API project and the support-bot project are available.
# By copying everything first, we allow dotnet restore to resolve
# dependencies across projects.
COPY . .

# Restore only the support-bot project. This implicitly restores
# referenced projects (like taskforge) because they are present in the
# context. Using the full path ensures dotnet finds the csproj.
RUN dotnet restore ./support-bot/support-bot.csproj

# Publish the support-bot project. This step compiles the project and
# outputs the published DLLs into the /app/publish directory.
RUN dotnet publish ./support-bot/support-bot.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Enable alternate stack check for improved stability (optional)
ENV DOTNET_EnableAlternateStackCheck=1

# Start the support bot. The DLL name must match the output assembly
# defined in support-bot.csproj (implicit via project name).
ENTRYPOINT ["dotnet", "support-bot.dll"]