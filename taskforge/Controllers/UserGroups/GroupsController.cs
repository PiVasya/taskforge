using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Constants;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.UserGroups
{
    [ApiController]
    [Route("api/groups")]
    [Authorize]
    public sealed class GroupsController : ControllerBase
    {
        private readonly IUserGroupService _groups;
        private readonly ICurrentUserService _current;

        public GroupsController(IUserGroupService groups, ICurrentUserService current)
        {
            _groups = groups;
            _current = current;
        }

        /// <summary>
        /// Список групп (для редакторов курсов). По умолчанию возвращает только активные.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool includeInactive = false)
        {
            var role = _current.GetRole();
            IReadOnlyList<taskforge.Data.Models.DTO.UserGroups.UserGroupDto> list;

            if (string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, AppRoles.Editor, StringComparison.OrdinalIgnoreCase))
            {
                // Admin/Editor видят все группы (при желании можно показать только активные)
                list = await _groups.GetAllAsync(includeInactive);
            }
            else
            {
                // Обычный пользователь видит только свои группы
                list = await _groups.GetForUserAsync(_current.GetUserId(), includeInactive);
            }
            return Ok(list);
        }
    }
}
