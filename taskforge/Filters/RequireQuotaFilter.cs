using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using taskforge.Constants;
using taskforge.Services.Interfaces;

namespace taskforge.Filters;

public sealed class RequireQuotaFilter : IAsyncActionFilter
{
    private readonly string _bucket;
    private readonly IQuotaService _quotas;
    private readonly ICurrentUserService _current;

    public RequireQuotaFilter(string bucket, IQuotaService quotas, ICurrentUserService current)
    {
        _bucket = bucket;
        _quotas = quotas;
        _current = current;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var role = _current.GetRole();
        if (IsPrivileged(role))
        {
            await next();
            return;
        }

        var userId = _current.GetUserId();
        var ct = context.HttpContext.RequestAborted;

        var res = await _quotas.TryConsumeAsync(userId, _bucket, ct);

        // отдаём заголовки — их удобно читать на фронте
        context.HttpContext.Response.Headers["X-Quota-Bucket"] = res.Bucket;
        context.HttpContext.Response.Headers["X-Quota-Remaining"] = res.Remaining.ToString();
        context.HttpContext.Response.Headers["X-Quota-Capacity"] = res.Capacity.ToString();
        context.HttpContext.Response.Headers["X-Quota-Retry-After"] = res.RetryAfterSeconds.ToString();
        context.HttpContext.Response.Headers["X-Quota-Next-Refill-At"] = res.NextRefillAtUtc.ToString("O");

        if (!res.Allowed)
        {
            context.HttpContext.Response.Headers["Retry-After"] = res.RetryAfterSeconds.ToString();
            context.Result = new ObjectResult(new
            {
                message = "Quota exceeded",
                bucket = res.Bucket,
                remaining = res.Remaining,
                capacity = res.Capacity,
                retryAfterSeconds = res.RetryAfterSeconds,
                nextRefillAtUtc = res.NextRefillAtUtc
            })
            {
                StatusCode = 429
            };
            return;
        }

        await next();
    }

    private static bool IsPrivileged(string? role)
    {
        return string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, AppRoles.Editor, StringComparison.OrdinalIgnoreCase);
    }
}
