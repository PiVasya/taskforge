using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services;

namespace taskforge.Controllers
{
    /// <summary>
    /// Controller for retrieving and updating the authenticated user's profile.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ProfileController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        private readonly PasswordHasher _passwordHasher;
        public ProfileController(ApplicationDbContext db, PasswordHasher passwordHasher)
        {
            _db = db;
            _passwordHasher = passwordHasher;
        }

        /// <summary>
        /// Returns the current user's profile.  The user must be authenticated.
        /// </summary>
        /// <returns>Full profile data including contact details and any custom fields.</returns>
        [HttpGet]
        [Authorize]
        public async Task<ActionResult<UserProfileDto>> GetProfile()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized();
            }

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return NotFound();

            var dto = new UserProfileDto
            {
                Id = user.Id,
                Email = user.Email,
                FirstName = user.FirstName,
                LastName = user.LastName,
                PhoneNumber = user.PhoneNumber,
                DateOfBirth = user.DateOfBirth,
                ProfilePictureUrl = user.ProfilePictureUrl,
                AdditionalDataJson = user.AdditionalDataJson
            };

            return Ok(dto);
        }

        /// <summary>
        /// Updates the current user's profile with provided fields.  Only non‑null fields will be applied.
        /// </summary>
        [HttpPut]
        [Authorize]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateUserProfileDto dto)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized();
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return NotFound();

            if (dto.FirstName != null)
                user.FirstName = dto.FirstName;
            if (dto.LastName != null)
                user.LastName = dto.LastName;
            if (dto.PhoneNumber != null)
                user.PhoneNumber = dto.PhoneNumber;
            if (dto.DateOfBirth.HasValue)
                user.DateOfBirth = dto.DateOfBirth.Value;
            if (dto.ProfilePictureUrl != null)
                user.ProfilePictureUrl = dto.ProfilePictureUrl;
            if (dto.AdditionalDataJson != null)
                user.AdditionalDataJson = dto.AdditionalDataJson;

            await _db.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Changes the current user's password. Requires the current password.
        /// </summary>
        [HttpPost("change-password")]
        [Authorize]
        public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            if (user.PasswordSalt == null || user.PasswordHash == null)
                return BadRequest(new { message = "Password is not set." });

            if (!_passwordHasher.VerifyPassword(dto.CurrentPassword, user.PasswordSalt, user.PasswordHash))
                return BadRequest(new { message = "Текущий пароль неверный." });

            var (salt, hash) = _passwordHasher.HashPassword(dto.NewPassword);
            user.PasswordSalt = salt;
            user.PasswordHash = hash;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { message = "Пароль изменён." });
        }

        /// <summary>
        /// Changes the current user's email. Requires password confirmation and checks uniqueness.
        /// </summary>
        [HttpPost("change-email")]
        [Authorize]
        public async Task<ActionResult> ChangeEmail([FromBody] ChangeEmailDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            if (string.Equals(user.Email, dto.NewEmail, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Новый email совпадает с текущим." });

            if (user.PasswordSalt == null || user.PasswordHash == null)
                return BadRequest(new { message = "Password is not set." });

            if (!_passwordHasher.VerifyPassword(dto.Password, user.PasswordSalt, user.PasswordHash))
                return BadRequest(new { message = "Неверный пароль." });

            var exists = await _db.Users.AnyAsync(u => u.Email == dto.NewEmail);
            if (exists)
                return Conflict(new { message = "Пользователь с таким email уже существует." });

            user.Email = dto.NewEmail;
            user.EmailConfirmed = false;
            user.EmailConfirmationToken = Guid.NewGuid();
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new { message = "Email обновлён. Подтвердите новый адрес, если требуется." });
        }

    }

}
