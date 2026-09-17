using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace TaskForge.Observability.Api.Services.Cluster;

public sealed record ClusterDiagnosticsStoredArchive(
    string Id,
    string Node,
    string JobId,
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class ClusterDiagnosticsArchiveStore(
    IAmazonS3 s3,
    IConfiguration configuration,
    ILogger<ClusterDiagnosticsArchiveStore> logger)
{
    private readonly IAmazonS3 _s3 = s3;
    private readonly ILogger<ClusterDiagnosticsArchiveStore> _logger = logger;
    private readonly string _bucket = Required(configuration, "S3:Bucket", "S3__Bucket");
    private readonly string _prefix = NormalizePrefix(configuration["ClusterDiagnosticsStorage:Prefix"] ?? "cluster-diagnostics");

    public int RetentionDays { get; } = Math.Clamp(configuration.GetValue("ClusterDiagnosticsStorage:RetentionDays", 7), 1, 30);

    public async Task<ClusterDiagnosticsStoredArchive> SaveAsync(
        string node,
        string jobId,
        string fileName,
        Stream input,
        string? contentType,
        long? contentLength,
        CancellationToken ct)
    {
        var safeNode = SafeSegment(node, 32, "node");
        var safeJob = SafeSegment(jobId, 80, "job");
        var safeFile = SafeFileName(fileName);
        var key = $"{_prefix}/{safeNode}/{safeJob}/{safeFile}";

        var put = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = input,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/gzip" : contentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        };
        // The Node Agent always serves a real archive file with Content-Length. Its
        // HTTP body stream is not seekable, so pass that exact length to AWSSDK.S3
        // instead of letting the SDK probe Stream.Position (which would fail).
        if (contentLength is > 0) put.Headers["Content-Length"] = contentLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (!input.CanSeek) throw new InvalidOperationException("Diagnostics archive stream is non-seekable and has no Content-Length.");
        await _s3.PutObjectAsync(put, ct);

        // Read metadata back from MinIO instead of trusting the upstream Content-Length.
        // This also verifies that the object is visible before we remove the in-memory job.
        var metadata = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucket,
            Key = key
        }, ct);
        var createdAt = DateTimeOffset.UtcNow;
        var archive = new ClusterDiagnosticsStoredArchive(
            EncodeId(key), safeNode, safeJob, safeFile, metadata.ContentLength,
            createdAt, createdAt.AddDays(RetentionDays));
        _logger.LogInformation(
            "Stored cluster diagnostics archive in MinIO: node={Node} job={JobId} key={Key} bytes={Bytes} retentionDays={RetentionDays}",
            safeNode, safeJob, key, metadata.ContentLength, RetentionDays);
        return archive;
    }

    public async Task<IReadOnlyList<ClusterDiagnosticsStoredArchive>> ListAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<ClusterDiagnosticsStoredArchive>();
        string? continuation = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _prefix + "/",
                ContinuationToken = continuation,
                MaxKeys = 1000
            }, ct);
            foreach (var item in page.S3Objects)
            {
                if (!TryParseKey(item.Key, out var node, out var jobId, out var fileName)) continue;
                var createdAt = new DateTimeOffset(item.LastModified.ToUniversalTime());
                var expiresAt = createdAt.AddDays(RetentionDays);
                if (expiresAt <= now) continue;
                result.Add(new ClusterDiagnosticsStoredArchive(
                    EncodeId(item.Key), node, jobId, fileName, item.Size, createdAt, expiresAt));
            }
            continuation = page.IsTruncated ? page.NextContinuationToken : null;
        } while (!string.IsNullOrWhiteSpace(continuation));

        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var expired = new List<string>();
        string? continuation = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _prefix + "/",
                ContinuationToken = continuation,
                MaxKeys = 1000
            }, ct);
            foreach (var item in page.S3Objects)
            {
                var createdAt = new DateTimeOffset(item.LastModified.ToUniversalTime());
                if (createdAt <= cutoff && TryParseKey(item.Key, out _, out _, out _)) expired.Add(item.Key);
            }
            continuation = page.IsTruncated ? page.NextContinuationToken : null;
        } while (!string.IsNullOrWhiteSpace(continuation));

        foreach (var key in expired)
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
        }
        if (expired.Count > 0)
            _logger.LogInformation("Deleted {Count} expired cluster diagnostics archives from MinIO (retention {RetentionDays} days).", expired.Count, RetentionDays);
        return expired.Count;
    }

    public async Task WriteDownloadAsync(string id, HttpResponse output, CancellationToken ct)
    {
        var key = DecodeId(id);
        if (!TryParseKey(key, out _, out _, out var fileName))
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_ARCHIVE_ID_INVALID", "Некорректный идентификатор сохранённого архива.");

        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ClusterDiagnosticsException(StatusCodes.Status404NotFound, "DIAGNOSTICS_ARCHIVE_NOT_FOUND", "Сохранённый архив не найден или срок его хранения истёк.");
        }

        using (response)
        {
            output.StatusCode = StatusCodes.Status200OK;
            output.ContentType = string.IsNullOrWhiteSpace(response.Headers.ContentType) ? "application/gzip" : response.Headers.ContentType;
            if (response.ContentLength >= 0) output.ContentLength = response.ContentLength;
            output.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
            output.Headers.CacheControl = "no-store";
            await response.ResponseStream.CopyToAsync(output.Body, ct);
        }
    }

    private bool TryParseKey(string key, out string node, out string jobId, out string fileName)
    {
        node = jobId = fileName = string.Empty;
        var prefix = _prefix + "/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = key[prefix.Length..].Split('/', 3, StringSplitOptions.None);
        if (parts.Length != 3) return false;
        try
        {
            node = SafeSegment(parts[0], 32, "node");
            jobId = SafeSegment(parts[1], 80, "job");
            fileName = SafeFileName(parts[2]);
            return string.Equals(parts[2], fileName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private string DecodeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1024)
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_ARCHIVE_ID_INVALID", "Некорректный идентификатор сохранённого архива.");
        try
        {
            var normalized = id.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var key = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            if (!key.StartsWith(_prefix + "/", StringComparison.Ordinal)) throw new FormatException();
            return key;
        }
        catch (FormatException)
        {
            throw new ClusterDiagnosticsException(StatusCodes.Status400BadRequest, "DIAGNOSTICS_ARCHIVE_ID_INVALID", "Некорректный идентификатор сохранённого архива.");
        }
    }

    private static string EncodeId(string key)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizePrefix(string value)
    {
        var prefix = value.Trim().Trim('/');
        if (prefix.Length is < 1 or > 96 || prefix.Split('/').Any(part => part.Length == 0 || part.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_')))
            throw new InvalidOperationException("ClusterDiagnosticsStorage:Prefix contains unsafe characters.");
        return prefix;
    }

    private static string SafeSegment(string value, int maxLength, string label)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length is < 1 || text.Length > maxLength || text.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new InvalidOperationException($"Unsafe diagnostics {label} segment.");
        return text;
    }

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value ?? string.Empty);
        var cleaned = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());
        if (cleaned.Length is < 1 or > 180) return "taskforge_diagnostics.tar.gz";
        return cleaned;
    }

    private static string Required(IConfiguration configuration, string key, string envKey)
    {
        var value = configuration[key] ?? configuration[envKey];
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Missing required configuration: {key}");
        return value.Trim();
    }
}
