namespace TaskForge.Tasks.Api.Domain;

public sealed class TaskGraphTransfer
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ImportOperationId { get; set; }
    public string Kind { get; set; } = "export";
    public string Lane { get; set; } = "metadata";
    public string State { get; set; } = "queued";
    public string RequestHash { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Phase { get; set; } = "queued";
    public int Percent { get; set; }
    public int CompletedItems { get; set; }
    public int TotalItems { get; set; }
    public long BytesWritten { get; set; }
    public long ResultBytes { get; set; }
    public string? CurrentItem { get; set; }
    public string? ErrorJson { get; set; }
    public int? ResultStatus { get; set; }
    public bool CancelRequested { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
}

// Compressed JSON is bounded into independently persisted parts. Polling never
// reads these bytes. A result becomes readable only after the final receipt.
public sealed class TaskGraphTransferPart
{
    public Guid TransferId { get; set; }
    public int Sequence { get; set; }
    public byte[] Data { get; set; } = [];
}
