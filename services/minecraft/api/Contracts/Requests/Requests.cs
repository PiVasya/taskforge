using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;


namespace TaskForge.Minecraft.Api.Contracts;

public sealed record MinecraftConfirmRequest(string? Code, string? PlayerName, string? PlayerUuid);

public sealed record MinecraftChatRequest(string? Author, string? Text);

public sealed record UserIdsRequest(Guid[] UserIds);
