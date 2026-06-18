using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;


namespace TaskForge.Ai.Api.Contracts;

public sealed record AgentClaimNextRequest(string WorkerId);

public sealed record AgentWorkerRequest(string WorkerId);

public sealed record AgentStepRequest(string WorkerId, JsonElement? Step);

public sealed record AgentCompleteRequest(string WorkerId, JsonElement? Result);

public sealed record AgentFailRequest(string WorkerId, JsonElement? Error);

public sealed record AgentRunTestsRequest(Guid? RunId, string? WorkerId, string? Language, string? Code, JsonElement? TestCases);
