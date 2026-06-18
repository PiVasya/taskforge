using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;

using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Hubs;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;
using static TaskForge.Minecraft.Api.Services.Mapping.MinecraftApiMappingService;
using static TaskForge.Minecraft.Api.Services.Serialization.MinecraftApiSerializationService;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-minecraft-api" }));

        app.MapGet("/health/ready", async (MinecraftDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-minecraft-api" }) : Results.StatusCode(503));

        app.MapGet("/", () => Results.Ok(new { service = "taskforge-minecraft-api", database = "taskforge_minecraft", status = "minecraft microservice active" }));

        app.MapGet("/api/minecraft/schema-owner", () => Results.Ok(new { database = "taskforge_minecraft", ownedEntities = new[] { "MinecraftLink", "MinecraftChatMessage" } }));

        return app;
    }
}
