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
    private static WebApplication MapChatEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/minecraft/chat/meta", () => Microsoft.AspNetCore.Http.Results.Ok(new { enabled = true, maxLength = 500 }));

        app.MapGet("/api/integrations/minecraft/chat/messages", async (MinecraftDbContext db, CancellationToken ct) => Microsoft.AspNetCore.Http.Results.Ok(await db.ChatMessages.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).OrderBy(x => x.CreatedAt).ToListAsync(ct)));

        app.MapPost("/api/integrations/minecraft/chat/messages", async (MinecraftChatRequest req, MinecraftDbContext db, CancellationToken ct) => { var msg = new MinecraftChatMessage { Author = string.IsNullOrWhiteSpace(req.Author) ? "web" : req.Author!, Text = req.Text ?? string.Empty }; db.ChatMessages.Add(msg); await db.SaveChangesAsync(ct); return Microsoft.AspNetCore.Http.Results.Ok(msg); });

        app.MapHub<MinecraftChatHub>("/hubs/minecraft-chat");

        return app;
    }
}
