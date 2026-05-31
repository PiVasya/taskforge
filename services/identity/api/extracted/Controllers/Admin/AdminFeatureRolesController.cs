using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/feature-roles")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminFeatureRolesController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IFeatureRoleService _roles;
        private readonly ICurrentUserService _current;

        public AdminFeatureRolesController(ApplicationDbContext db, IFeatureRoleService roles, ICurrentUserService current)
        {
            _db = db;
            _roles = roles;
            _current = current;
        }

        public sealed record FeatureRoleDto(Guid Id, string Code, string Name, string? Description, bool IsActive, int MembersCount);
        public sealed record FeatureRoleEditDto(string Code, string Name, string? Description, bool IsActive);
        public sealed record AssignFeatureRoleDto(string Code);
        public sealed record FeatureRoleUserDto(Guid Id, string Email, string FullName, string BaseRole, string? MinecraftNick, IReadOnlyList<string> FeatureRoles);

        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct)
        {
            await _roles.EnsureDefaultRolesAsync(ct);
            var items = await _db.FeatureRoles
                .AsNoTracking()
                .OrderBy(x => x.Code)
                .Select(x => new FeatureRoleDto(
                    x.Id,
                    x.Code,
                    x.Name,
                    x.Description,
                    x.IsActive,
                    x.Users.Count()))
                .ToListAsync(ct);
            return Ok(items);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] FeatureRoleEditDto dto, CancellationToken ct)
        {
            var code = (dto.Code ?? string.Empty).Trim();
            var name = (dto.Name ?? string.Empty).Trim();
            if (code.Length == 0 || name.Length == 0) return BadRequest(new { message = "Code и Name обязательны." });

            var exists = await _db.FeatureRoles.AnyAsync(x => x.Code.ToLower() == code.ToLower(), ct);
            if (exists) return Conflict(new { message = "Роль с таким кодом уже существует." });

            var role = new FeatureRole
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = name,
                Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
                IsActive = dto.IsActive,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _db.FeatureRoles.Add(role);
            await _db.SaveChangesAsync(ct);
            return Ok(new FeatureRoleDto(role.Id, role.Code, role.Name, role.Description, role.IsActive, 0));
        }

        [HttpPut("{roleId:guid}")]
        public async Task<IActionResult> Update(Guid roleId, [FromBody] FeatureRoleEditDto dto, CancellationToken ct)
        {
            var role = await _db.FeatureRoles.FirstOrDefaultAsync(x => x.Id == roleId, ct);
            if (role == null) return NotFound();

            var code = (dto.Code ?? string.Empty).Trim();
            var name = (dto.Name ?? string.Empty).Trim();
            if (code.Length == 0 || name.Length == 0) return BadRequest(new { message = "Code и Name обязательны." });

            var duplicate = await _db.FeatureRoles.AnyAsync(x => x.Id != roleId && x.Code.ToLower() == code.ToLower(), ct);
            if (duplicate) return Conflict(new { message = "Роль с таким кодом уже существует." });

            role.Code = code;
            role.Name = name;
            role.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            role.IsActive = dto.IsActive;
            role.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        [HttpDelete("{roleId:guid}")]
        public async Task<IActionResult> Delete(Guid roleId, CancellationToken ct)
        {
            var role = await _db.FeatureRoles.FirstOrDefaultAsync(x => x.Id == roleId, ct);
            if (role == null) return NotFound();

            var links = await _db.UserFeatureRoles.Where(x => x.RoleId == roleId).ToListAsync(ct);
            if (links.Count > 0) _db.UserFeatureRoles.RemoveRange(links);
            _db.FeatureRoles.Remove(role);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        [HttpGet("users")]
        public async Task<IActionResult> SearchUsers([FromQuery] string? query, CancellationToken ct)
        {
            var q = (query ?? string.Empty).Trim().ToLower();
            var usersQuery = _db.Users.AsNoTracking().AsQueryable();
            if (q.Length > 0)
            {
                usersQuery = usersQuery.Where(u =>
                    u.Email.ToLower().Contains(q) ||
                    (u.FirstName + " " + u.LastName).ToLower().Contains(q) ||
                    ((u.MinecraftNick ?? string.Empty).ToLower().Contains(q)));
            }

            var users = await usersQuery
                .OrderBy(u => u.Email)
                .Take(30)
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.FirstName,
                    u.LastName,
                    u.Role,
                    u.MinecraftNick
                })
                .ToListAsync(ct);

            var ids = users.Select(x => x.Id).ToList();
            var roleMap = await _db.UserFeatureRoles
                .AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.Role.IsActive)
                .Select(x => new { x.UserId, x.Role.Code })
                .ToListAsync(ct);

            var result = users.Select(u => new FeatureRoleUserDto(
                u.Id,
                u.Email,
                ($"{u.FirstName} {u.LastName}").Trim(),
                u.Role,
                u.MinecraftNick,
                roleMap.Where(x => x.UserId == u.Id).Select(x => x.Code).OrderBy(x => x).ToList()
            )).ToList();

            return Ok(result);
        }

        [HttpGet("users/{userId:guid}")]
        public async Task<IActionResult> GetUser(Guid userId, CancellationToken ct)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user == null) return NotFound();
            var roles = await _roles.GetRoleCodesForUserAsync(userId, ct);
            return Ok(new FeatureRoleUserDto(
                user.Id,
                user.Email,
                ($"{user.FirstName} {user.LastName}").Trim(),
                user.Role,
                user.MinecraftNick,
                roles
            ));
        }

        [HttpPost("users/{userId:guid}/roles")]
        public async Task<IActionResult> Assign(Guid userId, [FromBody] AssignFeatureRoleDto dto, CancellationToken ct)
        {
            var userExists = await _db.Users.AnyAsync(x => x.Id == userId, ct);
            if (!userExists) return NotFound(new { message = "Пользователь не найден." });

            await _roles.AssignRoleAsync(userId, dto.Code, _current.GetUserId(), ct);
            return NoContent();
        }

        [HttpDelete("users/{userId:guid}/roles/{code}")]
        public async Task<IActionResult> Remove(Guid userId, string code, CancellationToken ct)
        {
            await _roles.RemoveRoleAsync(userId, code, ct);
            return NoContent();
        }
    }
}
