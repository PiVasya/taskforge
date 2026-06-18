using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;


namespace TaskForge.Minecraft.Api.Hubs;

public sealed class MinecraftChatHub : Hub { }
