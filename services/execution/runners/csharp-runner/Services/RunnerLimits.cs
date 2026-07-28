using System.Text;
using Runner.Models;

namespace Runner.Services;

internal static class RunnerLimits
{
    public const long MaxRequestBytes = 16L * 1024 * 1024;
    public const int MaxCodeBytes = 1 * 1024 * 1024;
    public const int MaxInputBytes = 1 * 1024 * 1024;
    public const int MaxExpectedOutputBytes = 1 * 1024 * 1024;
    public const int MaxTestsPerRequest = 128;
    public const int MaxTotalTestDataBytes = 12 * 1024 * 1024;
    public const int MaxOutputChars = 1_000_000;
    public const int MinTimeLimitMs = 100;
    public const int MaxTimeLimitMs = 30_000;
    public const int MinMemoryLimitMb = 128;
    public const int MaxMemoryLimitMb = 512;

    public static string? Validate(RunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return "Code is empty.";
        }
        if (Utf8Length(request.Code) > MaxCodeBytes)
        {
            return "Code is too large.";
        }
        if (request.Input is not null && Utf8Length(request.Input) > MaxInputBytes)
        {
            return "Input is too large.";
        }
        return null;
    }

    public static string? Validate(RunRequestWithTests request)
    {
        var commonError = Validate((RunRequest)request);
        if (commonError is not null)
        {
            return commonError;
        }

        var tests = request.Tests ?? [];
        if (tests.Count > MaxTestsPerRequest)
        {
            return "Too many tests.";
        }

        long total = 0;
        foreach (var test in tests)
        {
            var inputBytes = Utf8Length(test.Input ?? string.Empty);
            var expectedBytes = Utf8Length(test.ExpectedOutput ?? string.Empty);
            if (inputBytes > MaxInputBytes)
            {
                return "Test input is too large.";
            }
            if (expectedBytes > MaxExpectedOutputBytes)
            {
                return "Expected output is too large.";
            }
            total += inputBytes + expectedBytes;
            if (total > MaxTotalTestDataBytes)
            {
                return "Test data is too large.";
            }
        }
        return null;
    }

    public static int TimeLimitMs(int? value)
    {
        var milliseconds = value.GetValueOrDefault(8_000);
        if (milliseconds <= 0)
        {
            milliseconds = 8_000;
        }
        return Math.Clamp(milliseconds, MinTimeLimitMs, MaxTimeLimitMs);
    }

    public static int MemoryLimitMb(int? value)
    {
        var megabytes = value.GetValueOrDefault(512);
        if (megabytes <= 0)
        {
            megabytes = 512;
        }
        return Math.Clamp(megabytes, MinMemoryLimitMb, MaxMemoryLimitMb);
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
}

public sealed class RunnerJobGate
{
    private readonly SemaphoreSlim _semaphore;

    public RunnerJobGate()
    {
        // A shared UID and /tmp are used inside a runner container, so submissions
        // must stay serial unless the deployment moves to one sandbox per job.
        _semaphore = new SemaphoreSlim(1, 1);
    }

    public Task EnterAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);
    public void Exit() => _semaphore.Release();
}
