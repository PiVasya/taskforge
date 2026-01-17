using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.UserGroups
{
    [ApiController]
    [Route("api/groups")]
    [Authorize(Roles = "Admin,Editor")]
    public sealed class GroupsController : ControllerBase
    {
        private readonly IUserGroupService _groups;

        public GroupsController(IUserGroupService groups)
        {
            _groups = groups;
        }

        /// <summary>
        /// Список групп (для редакторов курсов). По умолчанию возвращает только активные.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool includeInactive = false)
        {
            var list = await _groups.GetAllAsync(includeInactive);
            return Ok(list);
        }
    }
}
