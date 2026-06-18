using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Services.Mapping;

internal static class ExecutionApiMappingService
{
    internal static object ToJobDto(ExecutionJob x) => new { x.Id, x.SubmissionId, x.AssignmentId, x.UserId, x.Language, x.Code, x.Input, x.TestsJson, x.CodeForbiddenCallsJson, x.CodeRequiredCallsJson, x.TimeLimitMs, x.MemoryLimitMb, x.AttemptCount, x.Status, x.CreatedAt, x.StartedAt, x.CompletedAt };

}
