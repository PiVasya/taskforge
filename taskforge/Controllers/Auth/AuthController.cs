using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System;
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
        private const string AccessTokenCookie = "tf_at";
        private const string RefreshTokenCookie = "tf_rt";

        private readonly ApplicationDbContext _context;
        private readonly PasswordHasher _passwordHasher;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        /// <summary>
        /// Конструктор с внедрением контекста БД, хэш‑сервиса и конфигурации (для JWT).
        /// </summary>
        public AuthController(ApplicationDbContext context, PasswordHasher passwordHasher, IConfiguration configuration, IWebHostEnvironment env)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _configuration = configuration;
            _env = env;
        }

        /// <summary>
        /// Регистрация нового пользователя.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterUserDto dto)
        {
            var email = dto.Email.Trim().ToLowerInvariant();

            if (await _context.Users.AnyAsync(u => u.Email.ToLower() == email))
                return BadRequest("Пользователь с таким email уже существует.");

            var user = new User
            {
                Email = email,
                Name = (dto.FirstName + " " + dto.LastName).Trim(),
                PasswordHash = _passwordHasher.Hash(dto.Password),
                Role = UserRole.User
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Пользователь зарегистрирован" });
        }

        /// <summary>
        /// Авторизация пользователя. Токены выдаются и сохраняются в HttpOnly cookies.
        /// Дополнительно accessToken возвращается в ответе, чтобы фронт мог держать его в памяти (не localStorage).
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            var email = dto.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);

            if (user == null || !_passwordHasher.Verify(dto.Password, user.PasswordHash))
                return Unauthorized("Неверный email или пароль.");

            var accessMinutes = _configuration.GetValue<int?>("Jwt:ExpireMinutes") ?? 120;
            var accessTokenLifetime = TimeSpan.FromMinutes(accessMinutes);
            var refreshTokenLifetime = TimeSpan.FromDays(7);

            var accessToken = CreateJwt(user, accessTokenLifetime, tokenType: "access");
            var refreshToken = CreateJwt(user, refreshTokenLifetime, tokenType: "refresh");

            SetAuthCookies(accessToken, refreshToken, accessTokenLifetime, refreshTokenLifetime);

            return Ok(new { accessToken });
        }

        /// <summary>
        /// Обновление access token по refresh token из cookie.
        /// </summary>
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            if (!Request.Cookies.TryGetValue(RefreshTokenCookie, out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
                return Unauthorized();

            var principal = ValidateJwt(refreshToken, validateLifetime: true);
            if (principal == null)
                return Unauthorized();

            var tokenType = principal.FindFirstValue("token_type");
            if (!string.Equals(tokenType, "refresh", StringComparison.OrdinalIgnoreCase))
                return Unauthorized();

            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return Unauthorized();

            var accessMinutes = _configuration.GetValue<int?>("Jwt:ExpireMinutes") ?? 120;
            var accessTokenLifetime = TimeSpan.FromMinutes(accessMinutes);
            var refreshTokenLifetime = TimeSpan.FromDays(7);

            // Rotate refresh token (simple rotation without DB storage).
            var newAccessToken = CreateJwt(user, accessTokenLifetime, tokenType: "access");
            var newRefreshToken = CreateJwt(user, refreshTokenLifetime, tokenType: "refresh");

            SetAuthCookies(newAccessToken, newRefreshToken, accessTokenLifetime, refreshTokenLifetime);

            return Ok(new { accessToken = newAccessToken });
        }

        /// <summary>
        /// Выход: очищает cookies с токенами.
        /// </summary>
        [HttpPost("logout")]
        public IActionResult Logout()
        {
            ClearAuthCookies();
            return Ok();
        }

        private string CreateJwt(User user, TimeSpan lifetime, string tokenType)
        {
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                // Keep both forms to be safe (some parts read ClaimTypes.Role, some read "role")
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim("role", user.Role.ToString()),
                new Claim("token_type", tokenType)
            };

            var creds = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.Add(lifetime),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private ClaimsPrincipal? ValidateJwt(string token, bool validateLifetime)
        {
            try
            {
                var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!);

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = validateLifetime,
                    ValidIssuer = _configuration["Jwt:Issuer"],
                    ValidAudience = _configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    RoleClaimType = "role",
                    NameClaimType = ClaimTypes.NameIdentifier,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, validationParameters, out _);
                return principal;
            }
            catch
            {
                return null;
            }
        }

        private void SetAuthCookies(string accessToken, string refreshToken, TimeSpan accessLifetime, TimeSpan refreshLifetime)
        {
            var accessOptions = BuildAuthCookieOptions(DateTimeOffset.UtcNow.Add(accessLifetime));
            var refreshOptions = BuildAuthCookieOptions(DateTimeOffset.UtcNow.Add(refreshLifetime));

            Response.Cookies.Append(AccessTokenCookie, accessToken, accessOptions);
            Response.Cookies.Append(RefreshTokenCookie, refreshToken, refreshOptions);
        }

        private void ClearAuthCookies()
        {
            var options = BuildAuthCookieOptions(DateTimeOffset.UtcNow.AddDays(-1));
            Response.Cookies.Delete(AccessTokenCookie, options);
            Response.Cookies.Delete(RefreshTokenCookie, options);
        }

        private CookieOptions BuildAuthCookieOptions(DateTimeOffset expires)
        {
            // In local dev (http://localhost) Secure cookies are not sent.
            var secure = !_env.IsDevelopment() && !_env.IsEnvironment("Local");

            return new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = expires
            };
        }
    }
}
