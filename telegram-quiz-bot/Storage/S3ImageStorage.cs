using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using TelegramQuizBot.Configuration;

namespace TelegramQuizBot.Storage;

public sealed class S3ImageStorage : IS3ImageStorage
{
    private readonly S3Options _s3;
    private readonly TelegramQuizOptions _quiz;
    private readonly ILogger<S3ImageStorage> _logger;

    public S3ImageStorage(IOptions<S3Options> s3, IOptions<TelegramQuizOptions> quiz, ILogger<S3ImageStorage> logger)
    {
        _s3 = s3.Value;
        _quiz = quiz.Value;
        _logger = logger;
    }

    public async Task<string> SaveImageAsync(Stream stream, string contentType, string extension, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s3.AccessKey) || string.IsNullOrWhiteSpace(_s3.SecretKey))
            throw new InvalidOperationException("S3 access key or secret key is not configured.");

        var normalizedExtension = extension.StartsWith('.') ? extension : "." + extension;
        var key = $"{_quiz.ImagePrefix.TrimEnd('/')}/{Guid.NewGuid():N}{normalizedExtension}";

        var config = new AmazonS3Config
        {
            ServiceURL = _s3.Endpoint,
            ForcePathStyle = _s3.UsePathStyle,
            AuthenticationRegion = _s3.Region
        };

        using var client = new AmazonS3Client(new BasicAWSCredentials(_s3.AccessKey, _s3.SecretKey), config);
        var request = new PutObjectRequest
        {
            BucketName = _s3.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType
        };

        await client.PutObjectAsync(request, ct);
        _logger.LogInformation("Saved Telegram quiz image to S3 key {Key}", key);
        return key;
    }
}
