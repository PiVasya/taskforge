// YugabyteDB v2026.1 may reject Npgsql EF Core's ACCESS EXCLUSIVE table lock.
// This replacement is registered ONLY for a dedicated, one-shot migration Job,
// where Kubernetes guarantees one Job per database. Never register it for APIs
// or general-purpose concurrent migration runners.
#pragma warning disable EF1001
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

namespace TaskForge.Yugabyte;

public sealed class SingleJobYugabyteHistoryRepository(HistoryRepositoryDependencies dependencies)
    : NpgsqlHistoryRepository(dependencies)
{
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
        => new SingleJobLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationsDatabaseLock>(new SingleJobLock(this));

    private sealed class SingleJobLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => historyRepository;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#pragma warning restore EF1001
