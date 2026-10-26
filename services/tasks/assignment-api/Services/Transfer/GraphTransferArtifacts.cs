using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Services.Transfer;

internal static class GraphTransferArtifacts
{
    internal const int PartBytes = 256 * 1024;
    internal static async Task<long> WriteAsync(Guid id, object? result, IServiceScopeFactory scopes,
        GraphTransferProgress progress, CancellationToken ct)
    {
        await using var output = new PartWriter(id, scopes, progress, ct);
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gzip, result, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        await output.FlushAsync(ct);
        return output.Length;
    }

    internal static async Task CopyJsonAsync(Guid id, IServiceScopeFactory scopes, Stream destination, CancellationToken ct)
    {
        await using var source = new PartReader(id, scopes);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await gzip.CopyToAsync(destination, 64 * 1024, ct);
    }

    private sealed class PartWriter(Guid id, IServiceScopeFactory scopes, GraphTransferProgress progress, CancellationToken token) : Stream
    {
        private readonly byte[] buffer = new byte[PartBytes];
        private int used, sequence;
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int n) => WriteAsync(b.AsMemory(o, n), token).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            token.ThrowIfCancellationRequested();
            while (!data.IsEmpty)
            {
                var count = global::System.Math.Min(data.Length, buffer.Length - used);
                data.Slice(0, count).CopyTo(buffer.AsMemory(used));
                used += count; length += count; data = data.Slice(count);
                if (length > 512L * 1024 * 1024) throw new InvalidOperationException("TRANSFER_RESULT_TOO_LARGE");
                if (used == buffer.Length) await FlushAsync(ct);
            }
        }
        public override async Task FlushAsync(CancellationToken ct)
        {
            if (used == 0) return;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            db.TaskGraphTransferParts.Add(new TaskGraphTransferPart { TransferId = id, Sequence = sequence++, Data = buffer.AsSpan(0, used).ToArray() });
            await db.SaveChangesAsync(ct);
            used = 0;
            progress.Report("result.write", 95, bytes: length);
        }
        public override void Flush() => FlushAsync(token).GetAwaiter().GetResult();
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed class PartReader(Guid id, IServiceScopeFactory scopes) : Stream
    {
        private byte[] buffer = [];
        private int offset, sequence;
        private bool ended;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken ct = default)
        {
            if (target.Length == 0 || ended) return 0;
            if (offset == buffer.Length)
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                var part = await db.TaskGraphTransferParts.AsNoTracking().Where(x => x.TransferId == id && x.Sequence == sequence)
                    .Select(x => x.Data).SingleOrDefaultAsync(ct);
                if (part is null) { ended = true; return 0; }
                buffer = part; offset = 0; sequence++;
            }
            var count = global::System.Math.Min(buffer.Length - offset, target.Length);
            buffer.AsMemory(offset, count).CopyTo(target); offset += count;
            return count;
        }
        public override int Read(byte[] b, int o, int n) => ReadAsync(b.AsMemory(o, n)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
