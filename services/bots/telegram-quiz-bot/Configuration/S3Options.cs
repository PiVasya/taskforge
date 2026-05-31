namespace TelegramQuizBot.Configuration;

public sealed class S3Options
{
    public string Endpoint { get; set; } = "http://minio:9000";
    public string Bucket { get; set; } = "taskforge";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public bool UsePathStyle { get; set; } = true;
}
