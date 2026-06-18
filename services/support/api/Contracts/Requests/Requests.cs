using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;


namespace TaskForge.Support.Api.Contracts;

public sealed record SupportRequest(string? Subject, string? Message, string? Text);

public sealed record UserIdsRequest(Guid[] UserIds);
