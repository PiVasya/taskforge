using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO.Solutions;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/judge")]
    [Authorize]
    public sealed class JudgeController : ControllerBase
    {
        private readonly IJudgeService _judge;
        private readonly ICurrentUserService _current;

        public JudgeController(IJudgeService judge, ICurrentUserService current)
        {
            _judge = judge;
            _current = current;
        }

        [HttpPost("run")]
        public async Task<ActionResult<JudgeResponseDto>> Run([FromBody] JudgeRequestDto req)
        {
            if (req == null || req.AssignmentId == Guid.Empty || string.IsNullOrWhiteSpace(req.Source))
                return BadRequest(new { code = "VALIDATION_ERROR", message = "Неверные параметры запуска" });

            var userId = _current.GetUserId();
            var res = await _judge.JudgeAsync(req, userId);
            return Ok(res); // compile/runtime — это бизнес‑результат, не 4xx
        }
    }
}
