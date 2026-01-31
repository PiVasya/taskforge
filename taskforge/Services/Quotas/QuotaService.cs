using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Quotas;

public sealed class QuotaService : IQuotaService
{
    private readonly ApplicationDbContext _db;

    private sealed record BucketConfig(string Bucket, int Capacity, TimeSpan RefillInterval);

    // NOTE: Named arguments in C# are case-sensitive.
    private static readonly BucketConfig TasksCfg = new(QuotaBuckets.Tasks, Capacity: 5, RefillInterval: TimeSpan.FromSeconds(30));
    private static readonly BucketConfig TopCfg = new(QuotaBuckets.Top, Capacity: 1, RefillInterval: TimeSpan.FromMinutes(30));

    public QuotaService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<QuotaConsumeResult> TryConsumeAsync(Guid userId, string bucket, CancellationToken ct)
    {
        var cfg = GetConfig(bucket);
        var now = DateTime.UtcNow;

        // несколько попыток на случай гонки по RowVersion
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var e = await _db.UserQuotaBuckets
                .SingleOrDefaultAsync(x => x.UserId == userId && x.BucketType == cfg.Bucket, ct);

            if (e is null)
            {
                e = new UserQuotaBucket
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    BucketType = cfg.Bucket,
                    Tokens = cfg.Capacity,
                    LastRefillAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _db.UserQuotaBuckets.Add(e);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // кто-то создал параллельно — просто ретраим чтение
                    _db.ChangeTracker.Clear();
                    continue;
                }
            }

            var (tokensAfterRefill, lastRefillAtAfter) = ApplyRefillView(e.Tokens, e.LastRefillAtUtc, now, cfg.Capacity, cfg.RefillInterval);

            if (tokensAfterRefill <= 0)
            {
                var nextRefillAt = lastRefillAtAfter + cfg.RefillInterval;
                var retry = Math.Max(1, (int)Math.Ceiling((nextRefillAt - now).TotalSeconds));

                // обновим только если реально был refill (чтобы не спамить UPDATE)
                if (lastRefillAtAfter != e.LastRefillAtUtc)
                {
                    e.Tokens = tokensAfterRefill;
                    e.LastRefillAtUtc = lastRefillAtAfter;
                    e.UpdatedAtUtc = now;

                    try { await _db.SaveChangesAsync(ct); }
                    catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); continue; }
                }

                return new QuotaConsumeResult(
                    Allowed: false,
                    Bucket: cfg.Bucket,
                    Remaining: 0,
                    Capacity: cfg.Capacity,
                    RetryAfterSeconds: retry,
                    NextRefillAtUtc: nextRefillAt);
            }

            // consume 1 token
            var newTokens = tokensAfterRefill - 1;
            e.Tokens = newTokens;
            e.LastRefillAtUtc = lastRefillAtAfter; // consumption не меняет "якорь" восстановления
            e.UpdatedAtUtc = now;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                continue;
            }

            var nextAt = lastRefillAtAfter + cfg.RefillInterval;
            var retryAfter = newTokens > 0 ? 0 : Math.Max(1, (int)Math.Ceiling((nextAt - now).TotalSeconds));

            return new QuotaConsumeResult(
                Allowed: true,
                Bucket: cfg.Bucket,
                Remaining: newTokens,
                Capacity: cfg.Capacity,
                RetryAfterSeconds: retryAfter,
                NextRefillAtUtc: nextAt);
        }

        // если совсем не повезло с конкуренцией — считаем что нельзя (безопасно)
        return new QuotaConsumeResult(
            Allowed: false,
            Bucket: cfg.Bucket,
            Remaining: 0,
            Capacity: cfg.Capacity,
            RetryAfterSeconds: 1,
            NextRefillAtUtc: now.AddSeconds(1));
    }

    public async Task<(QuotaConsumeResult Tasks, QuotaConsumeResult Top)> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        async Task<QuotaConsumeResult> StatusFor(BucketConfig cfg)
        {
            var e = await _db.UserQuotaBuckets.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId && x.BucketType == cfg.Bucket, ct);

            if (e is null)
            {
                return new QuotaConsumeResult(
                    Allowed: true,
                    Bucket: cfg.Bucket,
                    Remaining: cfg.Capacity,
                    Capacity: cfg.Capacity,
                    RetryAfterSeconds: 0,
                    NextRefillAtUtc: now.Add(cfg.RefillInterval));
            }

            var (tokensAfter, lastRefillAfter) = ApplyRefillView(e.Tokens, e.LastRefillAtUtc, now, cfg.Capacity, cfg.RefillInterval);
            var nextAt = lastRefillAfter + cfg.RefillInterval;
            var retry = tokensAfter > 0 ? 0 : Math.Max(1, (int)Math.Ceiling((nextAt - now).TotalSeconds));

            return new QuotaConsumeResult(
                Allowed: tokensAfter > 0,
                Bucket: cfg.Bucket,
                Remaining: Math.Max(0, tokensAfter),
                Capacity: cfg.Capacity,
                RetryAfterSeconds: retry,
                NextRefillAtUtc: nextAt);
        }

        var tasks = await StatusFor(TasksCfg);
        var top = await StatusFor(TopCfg);
        return (tasks, top);
    }

    private static BucketConfig GetConfig(string bucket)
    {
        var b = (bucket ?? string.Empty).Trim().ToLowerInvariant();
        return b switch
        {
            "tasks" => TasksCfg,
            "top" => TopCfg,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unknown quota bucket")
        };
    }

    private static (int Tokens, DateTime LastRefillAt) ApplyRefillView(int tokens, DateTime lastRefillAtUtc, DateTime nowUtc, int capacity, TimeSpan refillInterval)
    {
        if (tokens >= capacity)
            return (capacity, lastRefillAtUtc);

        var elapsed = nowUtc - lastRefillAtUtc;
        if (elapsed <= TimeSpan.Zero)
            return (Math.Clamp(tokens, 0, capacity), lastRefillAtUtc);

        var refills = (int)Math.Floor(elapsed.TotalSeconds / refillInterval.TotalSeconds);
        if (refills <= 0)
            return (Math.Clamp(tokens, 0, capacity), lastRefillAtUtc);

        var newTokens = Math.Min(capacity, tokens + refills);
        var newLast = lastRefillAtUtc.AddSeconds(refills * refillInterval.TotalSeconds);
        return (newTokens, newLast);
    }
}
