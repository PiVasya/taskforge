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

namespace TaskForge.Minecraft.Api.Services.Serialization;

internal static class MinecraftApiSerializationService
{
    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

}
