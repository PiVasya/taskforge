using Microsoft.EntityFrameworkCore;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Data;

namespace TaskForge.Tasks.Api.Services.Transfer;

internal sealed class GraphTransferDownloadResult(Guid id, IServiceScopeFactory scopes, string? filename) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await using var guard = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-transfer:{id:D}", http.RequestAborted, shared: true);
        if (guard is null)
        {
            http.Response.StatusCode = 503;
            http.Response.Headers.RetryAfter = "1";
            await http.Response.WriteAsJsonAsync(new { code = "TRANSFER_RESULT_BUSY" }, http.RequestAborted);
            return;
        }
        var ready = await db.TaskGraphTransfers.AsNoTracking().AnyAsync(x => x.Id == id && x.State == "succeeded"
            && x.ExpiresAt > DateTimeOffset.UtcNow, http.RequestAborted);
        if (!ready) { http.Response.StatusCode = 410; return; }
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (filename is not null) http.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        await GraphTransferArtifacts.CopyJsonAsync(id, scopes, http.Response.Body, http.RequestAborted);
    }
}
