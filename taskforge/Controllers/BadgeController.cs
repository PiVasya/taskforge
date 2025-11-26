using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    /// <summary>
    /// API‑контроллер для работы с бейджами. Позволяет получать список бейджей,
    /// создавать новые и назначать их пользователям. Доступен только администраторам.
    /// </summary>
    [ApiController]
    [Route("api/badges")]
    [Authorize(Roles = "Admin")]
    public class BadgeController : ControllerBase
    {
        private readonly IBadgeService _badgeService;

        public BadgeController(IBadgeService badgeService)
        {
            _badgeService = badgeService;
        }

        /// <summary>
        /// Возвращает список всех существующих бейджей.
        /// </summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IReadOnlyList<BadgeDto>> GetAll()
        {
            return await _badgeService.GetAllBadgesAsync();
        }

        /// <summary>
        /// Возвращает список бейджей, присвоенных конкретному пользователю.
        /// </summary>
        [HttpGet("user/{userId:guid}")]
        [AllowAnonymous]
        public async Task<IReadOnlyList<BadgeDto>> GetUserBadges(Guid userId)
        {
            return await _badgeService.GetUserBadgesAsync(userId);
        }

        /// <summary>
        /// Создаёт новый бейдж. Принимает данные формы (multipart/form-data):
        /// имя, описание и SVG‑файл. Возвращает созданный бейдж.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create([FromForm] CreateBadgeRequest request)
        {
            var badge = await _badgeService.CreateBadgeAsync(request.Name, request.Description ?? string.Empty, request.File);
            return Ok(badge);
        }

        /// <summary>
        /// Назначает бейдж пользователю. Должен быть отправлен JSON с полями userId и badgeId.
        /// </summary>
        [HttpPost("award")]
        public async Task<IActionResult> Award([FromBody] AwardBadgeRequest request)
        {
            await _badgeService.AwardBadgeAsync(request.UserId, request.BadgeId);
            return Ok();
        }

        /// <summary>
        /// Модель запроса для создания бейджа.
        /// </summary>
        public sealed class CreateBadgeRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public IFormFile File { get; set; } = default!;
        }

        /// <summary>
        /// Модель запроса для назначения бейджа пользователю.
        /// </summary>
        public sealed class AwardBadgeRequest
        {
            public Guid UserId { get; set; }
            public Guid BadgeId { get; set; }
        }

        /// <summary>
        /// Удаляет бейдж. Доступно только администраторам. При удалении также
        /// удаляются все назначения этого бейджа пользователям и, если
        /// изображение хранится как файл, файл удаляется.
        /// </summary>
        /// <param name="badgeId">ID бейджа</param>
        [HttpDelete("{badgeId:guid}")]
        public async Task<IActionResult> Delete(Guid badgeId)
        {
            await _badgeService.DeleteBadgeAsync(badgeId);
            return NoContent();
        }
    }
}