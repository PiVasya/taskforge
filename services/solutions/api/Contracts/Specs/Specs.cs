using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;


namespace TaskForge.Solutions.Api.Contracts;

public sealed record JudgeSpec(Guid Id, string? Type, string? Language, string[]? AllowedLanguages, string[]? CodeForbiddenCalls, string[]? CodeRequiredCalls, JsonElement? Tests, JsonElement? TestCases, string? TestsJson);
