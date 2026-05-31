namespace taskforge.Services.Files;

/// <summary>
/// Настройки S3-совместимого хранилища (MinIO).
/// Прокидываются через переменные окружения в формате:
/// S3__Endpoint, S3__AccessKey, S3__SecretKey, S3__Bucket, S3__Region, S3__UsePathStyle
/// </summary>
public sealed class S3StorageOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Для MinIO почти всегда нужно ForcePathStyle=true.
    /// </summary>
    public bool UsePathStyle { get; set; } = true;
}
