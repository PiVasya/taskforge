using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.Support;
using taskforge.Extensions;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    /// <summary>
    /// Контроллер для работы с обращениями в поддержку.
    /// </summary>
    [ApiController]
    [Route("api/support")]
    [Authorize]
    public class SupportController : ControllerBase
    {
        private readonly ISupportService _support;

        public SupportController(ISupportService support)
        {
            _support = support;
        }

        [HttpGet]
        public async Task<IActionResult> GetTickets()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            bool isAdmin = UserPermissions.IsSupportAdmin(User);

            var tickets = await _support.GetTicketsAsync(userId, isAdmin, HttpContext.RequestAborted);
            return Ok(tickets);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetTicket(Guid id)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            bool isAdmin = UserPermissions.IsSupportAdmin(User);

            var dto = await _support.GetTicketAsync(id, userId, isAdmin, HttpContext.RequestAborted);
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateSupportTicketRequestDto request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            var ticketId = await _support.CreateTicketAsync(userId, request.Type, request.Message, HttpContext.RequestAborted);
            // Keep both fields for compatibility with different frontends.
            // Some clients expect `ticketId`, others expect `id`. Do not include both `id` and `Id` because JSON
            // serialization treats property names case-insensitively, which causes a collision and runtime exception.
            return Ok(new { ticketId, id = ticketId });
        }

        [HttpPost("{id:guid}")]
        public async Task<IActionResult> AddMessage(Guid id, [FromBody] AddSupportMessageRequestDto request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            bool isAdmin = UserPermissions.IsSupportAdmin(User);
            var adminName = User.Identity?.Name;
            var msgId = await _support.AddMessageAsync(id, userId, isAdmin, adminName, request.Message, HttpContext.RequestAborted);
            return Ok(new { Id = msgId });
        }

        [HttpPost("{id:guid}/close")]
        public async Task<IActionResult> CloseTicket(Guid id)
        {
            bool isAdmin = UserPermissions.IsSupportAdmin(User);
            await _support.CloseTicketAsync(id, isAdmin, HttpContext.RequestAborted);
            return Ok();
        }
    }
}