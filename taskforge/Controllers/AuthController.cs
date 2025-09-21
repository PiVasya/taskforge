using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using taskforge.Data;
using taskforge.Data.Models;
using taskforge.Services;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers
{
    /// <summary>
    /// Контроллер для регистрации и авторизации пользователей.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly PasswordHasher _passwordHasher;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Конструктор с внедрением контекста БД, хэш‑сервиса и конфигурации (для JWT).
        /// </summary>
        public AuthController(ApplicationDbContext context, PasswordHasher passwordHasher, IConfiguration configuration)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _configuration = configuration;
        }

        /// <summary>
        /// Регистрация нового пользователя.
        /// </summary>
        /// <param name="dto">Данные регистрации (email, имя, фамилия, пароль, телефон и дата рождения).</param>
        /// <returns>Результат регистрации.</returns>
        [AllowAnonymous]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterUserDto dto)
        {
            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
            {
                return BadRequest(new { message = "Email уже используется." });
            }

            // Хэшируем пароль (Argon2id используется внутри PasswordHasher)
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

            // Можно автоматически выдать токен после регистрации, но пока вернём лишь сообщение
            return Ok(new { message = "Регистрация успешна." });
        }

        /// <summary>
        /// Авторизация пользователя.
        /// </summary>
        /// <param name="dto">Данные для входа (email и пароль).</param>
        /// <returns>JWT‑токен либо сообщение об ошибке.</returns>
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            var user = await _context.Users.SingleOrDefaultAsync(u => u.Email == dto.Email);
            if (user == null)
            {
                return Unauthorized(new { message = "Неверные учетные данные." });
            }

            // Проверяем пароль
            bool isValid = _passwordHasher.VerifyPassword(dto.Password, user.PasswordSalt, user.PasswordHash);
            if (!isValid)
            {
                user.AccessFailedCount++;
                await _context.SaveChangesAsync();
                return Unauthorized(new { message = "Неверные учетные данные." });
            }

            // При успешном входе обновляем дату последнего входа и обнуляем счётчик ошибок
            user.LastLoginAt = DateTime.UtcNow;
            user.AccessFailedCount = 0;
            await _context.SaveChangesAsync();

            // Формируем список claims
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            // Подготовка ключа и параметров
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            // Задаём время жизни токена
            var expires = DateTime.UtcNow.AddMinutes(
                double.Parse(_configuration["Jwt:ExpireMinutes"] ?? "60"));

            // Создание токена
            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: expires,
                signingCredentials: creds
            );

            string tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            return Ok(new { token = tokenString });
        }
    }
}
