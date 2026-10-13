using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TaskForge.Shared.Import;

// A session lock spans the short journal transaction and the data transaction.
// PostgreSQL releases it when the connection/process dies; no expiring lease may
// admit a second writer while the first writer is still committing.
public sealed class PostgresImportLock : IAsyncDisposable
{
    private readonly DbContext db;
    private readonly long key;
    private PostgresImportLock(DbContext db, long key) { this.db = db; this.key = key; }

    public static async Task<PostgresImportLock?> TryAcquireAsync(DbContext db, string name, CancellationToken ct)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var key = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            var parameter = command.CreateParameter(); parameter.ParameterName = "key"; parameter.Value = key;
            command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(ct) is true) return new PostgresImportLock(db, key);
        }
        catch { await db.Database.CloseConnectionAsync(); throw; }
        await db.Database.CloseConnectionAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (db.Database.GetDbConnection().State == ConnectionState.Open)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.CommandTimeout = 5;
                var parameter = command.CreateParameter(); parameter.ParameterName = "key"; parameter.Value = key;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
