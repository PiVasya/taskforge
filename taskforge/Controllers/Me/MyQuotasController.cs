using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Me;

[ApiController]
[Route("api/me/quotas")]
[Authorize]
public sealed class MyQuotasController : ControllerBase
{
    private readonly IQuotaService _quotas;
    private readonly ICurrentUserService _current;

    public MyQuotasController(IQuotaService quotas, ICurrentUserService current)
    {
        _quotas = quotas;
        _current = current;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = _current.GetUserId();
        var (tasks, top) = await _quotas.GetStatusAsync(userId, ct);

        // Единый лимитер: "top" и "tasks" фактически одно и то же.
        // Возвращаем обе секции для обратной совместимости фронта.
        top = tasks;

        return Ok(new
        {
            unified = true,
            tasks = new
            {
                remaining = tasks.Remaining,
                capacity = tasks.Capacity,
                retryAfterSeconds = tasks.RetryAfterSeconds,
                nextRefillAtUtc = tasks.NextRefillAtUtc
            },
            top = new
            {
                remaining = top.Remaining,
                capacity = top.Capacity,
                retryAfterSeconds = top.RetryAfterSeconds,
                nextRefillAtUtc = top.NextRefillAtUtc
            }
        });
    }
}
