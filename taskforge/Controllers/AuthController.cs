using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using TaskForge.Data;
using TaskForge.Data.Models;
using TaskForge.Services;

namespace TaskForge.Controllers
{
    /// <summary>
    /// Контроллер для регистрации и авторизации пользователей.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly PasswordHasher _passwordHasher;

        /// <summary>
        /// Конструктор с внедрением контекста БД и сервиса хэширования паролей.
        /// </summary>
        public AuthController(ApplicationDbContext context, PasswordHasher passwordHasher)
        {
            _context = context;
            _passwordHasher = passwordHasher;
        }

        /// <summary>
        /// Регистрация нового пользователя.
        /// </summary>
        /// <param name="dto">Данные регистрации (email, имя, фамилия, пароль, телефон и дата рождения).</param>
        /// <returns>Результат регистрации.</returns>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterUserDto dto)
        {
            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
            {
                return BadRequest(new { message = "Email уже используется." });
            }

            // Хэшируем пароль с использованием Argon2id
            var (salt, hash) = _passwordHasher.HashPassword(dto.Password);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = dto.Email,
                FirstName = dto.FirstName,
                LastName = dto.LastName,
                PhoneNumber = dto.PhoneNumber,
                DateOfBirth = dto.DateOfBirth,
                PasswordSalt = salt,
                PasswordHash = hash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Регистрация успешна." });
        }

        /// <summary>
        /// Авторизация пользователя.
        /// </summary>
        /// <param name="dto">Данные для входа (email и пароль).</param>
        /// <returns>Результат авторизации.</returns>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            var user = await _context.Users.SingleOrDefaultAsync(u => u.Email == dto.Email);
            if (user == null)
            {
                return Unauthorized(new { message = "Неверные учетные данные." });
            }

            // Проверяем пароль с использованием Argon2id
            bool isValid = _passwordHasher.VerifyPassword(dto.Password, user.PasswordSalt, user.PasswordHash);
            if (!isValid)
            {
                user.AccessFailedCount++;
                await _context.SaveChangesAsync();
                return Unauthorized(new { message = "Неверные учетные данные." });
            }

            // При успешном входе обновляем дату последнего входа
            user.LastLoginAt = DateTime.UtcNow;
            user.AccessFailedCount = 0;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Вход выполнен успешно." });
        }
    }
}
