# syntax=docker/dockerfile:1

# Multi-stage Dockerfile for the support-bot service.
#
# This file builds and publishes the support-bot project in a minimal
# runtime image. The resulting container will run the bot, which
# listens to incoming Telegram messages via long polling and writes
# replies back into the TaskForge database.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files and restore dependencies separately for better
# Docker layer caching. We copy both the support-bot and taskforge
# project files because the bot references the taskforge project.
COPY taskforge/taskforge.csproj ./taskforge/
COPY support-bot/support-bot.csproj ./support-bot/

RUN dotnet restore support-bot/support-bot.csproj

# Copy the full source code into the image
COPY taskforge/ ./taskforge/
COPY support-bot/ ./support-bot/

# Publish the support-bot project. This command compiles the code and
# produces a self-contained set of binaries under /app/publish.
RUN dotnet publish support-bot/support-bot.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Enable alternate stack check for improved stability (optional)
ENV DOTNET_EnableAlternateStackCheck=1

# Start the support bot. The DLL name must match the output assembly
# defined in support-bot.csproj (implicit via project name).
ENTRYPOINT ["dotnet", "support-bot.dll"]