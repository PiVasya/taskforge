using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;


namespace TaskForge.Observability.Api.Contracts;

public sealed record PageViewRequest(string? Path, string? Url, string? Method, string? Action, int? StatusCode, long? DurationMs);

public sealed record UserIdsRequest(Guid[] UserIds);
