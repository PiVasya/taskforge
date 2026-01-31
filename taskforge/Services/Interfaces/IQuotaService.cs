using System;
using System.Threading;
using System.Threading.Tasks;

namespace taskforge.Services.Interfaces;

public sealed record QuotaConsumeResult(
    bool Allowed,
    string Bucket,
    int Remaining,
    int Capacity,
    int RetryAfterSeconds,
    DateTime NextRefillAtUtc);

public interface IQuotaService
{
    /// <summary>
    /// Пытается списать 1 токен из бакета пользователя.
    /// Если токенов нет — Allowed=false и RetryAfterSeconds > 0.
    /// </summary>
    Task<QuotaConsumeResult> TryConsumeAsync(Guid userId, string bucket, CancellationToken ct);

    /// <summary>
    /// Текущее состояние бакетов пользователя (без списания).
    /// </summary>
    Task<(QuotaConsumeResult Tasks, QuotaConsumeResult Top)> GetStatusAsync(Guid userId, CancellationToken ct);
}
