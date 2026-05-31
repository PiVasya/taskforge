using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.UserGroups;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/groups")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminGroupsController : ControllerBase
    {
        private readonly IUserGroupService _groups;

        public AdminGroupsController(IUserGroupService groups)
        {
            _groups = groups;
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool includeInactive = true)
        {
            var list = await _groups.GetAllAsync(includeInactive);
            return Ok(list);
        }

        [HttpGet("{groupId:guid}")]
        public async Task<IActionResult> Get([FromRoute] Guid groupId)
        {
            var dto = await _groups.GetByIdAsync(groupId);
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateUserGroupRequest request)
        {
            var id = await _groups.CreateAsync(request);
            return CreatedAtAction(nameof(Get), new { groupId = id }, new { id });
        }

        [HttpPut("{groupId:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid groupId, [FromBody] UpdateUserGroupRequest request)
        {
            await _groups.UpdateAsync(groupId, request);
            return NoContent();
        }

        [HttpDelete("{groupId:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid groupId)
        {
            await _groups.DeleteAsync(groupId);
            return NoContent();
        }

        [HttpPost("{groupId:guid}/members")]
        public async Task<IActionResult> AddMember([FromRoute] Guid groupId, [FromBody] SetGroupMemberRequest request)
        {
            await _groups.AddMemberAsync(groupId, request.UserId);
            return NoContent();
        }

        [HttpDelete("{groupId:guid}/members/{userId:guid}")]
        public async Task<IActionResult> RemoveMember([FromRoute] Guid groupId, [FromRoute] Guid userId)
        {
            await _groups.RemoveMemberAsync(groupId, userId);
            return NoContent();
        }
    }
}
