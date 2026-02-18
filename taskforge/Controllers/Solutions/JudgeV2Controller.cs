using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using taskforge.Constants;
using taskforge.Data.Models.DTO.Solutions;
using taskforge.Filters;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers;

/// <summary>
/// New controller for judging (v2). Same scenario as /api/judge/run,
/// but adds a pre-check through external code-analyzer.
/// Frontend stays unchanged; this is for direct testing on prod.
/// </summary>
[ApiController]
[Route("api/judge2")]
[Authorize]
public sealed class JudgeV2Controller : ControllerBase
{
    private readonly IPolicyJudgeService _judge;
    private readonly ICurrentUserService _current;

    public JudgeV2Controller(IPolicyJudgeService judge, ICurrentUserService current)
    {
        _judge = judge;
        _current = current;
    }

    [HttpPost("run")]
    [RequireQuota(QuotaBuckets.Tasks)]
    public async Task<ActionResult<JudgeResponseDto>> Run([FromBody] JudgeRequestDto req)
    {
        Console.WriteLine("[Judge2Controller] >>> POST /api/judge2/run");
        if (req == null || req.AssignmentId == Guid.Empty || string.IsNullOrWhiteSpace(req.Source))
        {
            Console.WriteLine("[Judge2Controller] validation failed");
            return BadRequest(new { code = "VALIDATION_ERROR", message = "Неверные параметры запуска" });
        }

        var userId = _current.GetUserId();
        Console.WriteLine($"[Judge2Controller] userId={userId} assignmentId={req.AssignmentId} lang='{req.Language}' src.len={req.Source?.Length ?? 0}");
        var res = await _judge.JudgeAsync(req, userId);
        Console.WriteLine($"[Judge2Controller] <<< status={res?.Status} message='{res?.Message ?? ""}'");
        return Ok(res);
    }
}
