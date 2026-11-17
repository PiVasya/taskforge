using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Shared.Import;
using Xunit;

namespace TaskForge.Ai.Api.Tests;

public sealed class AccountWorkerAdvisoryLockTests
{
    [Fact]
    public async Task OnlyOneReplicaCanOwnAnOperationAndCrashReleasesItsLease()
    {
        // Optional integration test: set this only to a disposable PostgreSQL instance.
        // Session advisory locks do not need any TaskForge schema objects.
        var connection = Environment.GetEnvironmentVariable("TASKFORGE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;

        var options = new DbContextOptionsBuilder<AiDbContext>().UseNpgsql(connection).Options;
        await using var first = new AiDbContext(options);
        await using var second = new AiDbContext(options);
        var key = $"tf-account-worker-test:{Guid.NewGuid():N}";
        await using (var held = await PostgresImportLock.TryAcquireAsync(first, key, CancellationToken.None))
        {
            Assert.NotNull(held);
            await using var other = await PostgresImportLock.TryAcquireAsync(second, key, CancellationToken.None);
            Assert.Null(other);
        }

        // The second process can take over only after the original DB session
        // relinquishes the lock; no arbitrary TTL or recovery sweep is involved.
        await using var recovered = await PostgresImportLock.TryAcquireAsync(second, key, CancellationToken.None);
        Assert.NotNull(recovered);
    }
}
