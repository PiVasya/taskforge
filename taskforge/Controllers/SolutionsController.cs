using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/assignments/{assignmentId:guid}")]
    [Authorize]
    public sealed class SolutionsController : ControllerBase
    {
        private readonly ISolutionService _solutions;
        private readonly ICurrentUserService _current;

        public SolutionsController(ISolutionService solutions, ICurrentUserService current)
        {
            _solutions = solutions;
            _current = current;
        }

        [HttpPost("submit")]
        public async Task<IActionResult> Submit([FromRoute] Guid assignmentId, [FromBody] SubmitSolutionRequest req)
        {
            var result = await _solutions.SubmitAsync(assignmentId, _current.GetUserId(), req);
            return Ok(result);
        }
    }
}
