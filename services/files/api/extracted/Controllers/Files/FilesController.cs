using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Services.Files;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Files;

[ApiController]
[Route("api")]
[Authorize]
public sealed class FilesController : ControllerBase
{
    private readonly IFileStorageService _store;
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _current;
    private readonly ICourseAccessService _access;

    public FilesController(IFileStorageService store, ApplicationDbContext db, ICurrentUserService current, ICourseAccessService access)
    {
        _store = store;
        _db = db;
        _current = current;
        _access = access;
    }

    /// <summary>
    /// Загрузка картинки для редактора условий (TipTap). Возвращает ключ и URL,
    /// который можно сразу вставлять в документ.
    /// </summary>
    [HttpPost("files/images")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage([FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null) return BadRequest(new { message = "Файл не передан" });

        try
        {
            var key = await _store.UploadImageAsync(file, "editor-images", ct);
            var url = $"/api/files/{Uri.EscapeDataString(key)}";
            return Ok(new { key, url });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Получить файл по ключу из S3. Ключ может содержать слеши.
    /// </summary>
    [HttpGet("files/{*key}")]
    [AllowAnonymous] // можно оставить публичным, но в UI доступ всё равно по страницам с JWT
    public async Task<IActionResult> Get([FromRoute] string key, CancellationToken ct)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(key ?? string.Empty);
            var (stream, contentType) = await _store.GetAsync(decoded, ct);

            // кеш на клиенте/прокси (можно изменить)
            Response.Headers.CacheControl = "public,max-age=31536000";

            return File(stream, contentType);
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { message = "Файл не найден" });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Приватная раздача файла по ключу. Доступ только по JWT.
    /// Используется для image-test (эталон и результаты), чтобы ключи нельзя было
    /// просто открывать без авторизации.
    /// </summary>
    [HttpGet("private-files/{*key}")]
    public async Task<IActionResult> GetPrivate([FromRoute] string key, CancellationToken ct)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(key ?? string.Empty);
            if (!await CanAccessPrivateFileAsync(decoded, ct))
                return Forbid();

            var (stream, contentType) = await _store.GetAsync(decoded, ct);

            Response.Headers.CacheControl = "private,max-age=0,no-store";
            return File(stream, contentType);
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { message = "Файл не найден" });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<bool> CanAccessPrivateFileAsync(string key, CancellationToken ct)
    {
        var userId = _current.GetUserId();
        if (_current.HasRole(AppRoles.Admin))
            return true;

        var normalizedKey = (key ?? string.Empty).Trim('/');
        const string agentPrefix = "agent-conversations/";
        if (normalizedKey.StartsWith(agentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalizedKey[agentPrefix.Length..];
            var slash = rest.IndexOf('/');
            var conversationPart = slash >= 0 ? rest[..slash] : rest;
            if (Guid.TryParseExact(conversationPart, "N", out var conversationId) || Guid.TryParse(conversationPart, out conversationId))
            {
                return await _db.AgentConversations.AsNoTracking()
                    .AnyAsync(x => x.Id == conversationId && x.UserId == userId && !x.IsArchived, ct);
            }
            return false;
        }

        if (await _db.UserImageTaskSolutions.AsNoTracking().AnyAsync(x => x.UserId == userId && x.SubmittedKey == key, ct))
            return true;


        var imageAssignments = await _db.TaskAssignments.AsNoTracking()
            .Where(a => a.ImageTestReferenceKey == key)
            .Select(a => a.CourseId)
            .ToListAsync(ct);

        var role = _current.GetRole();
        foreach (var courseId in imageAssignments)
        {
            if (await _access.CanViewCourseAsync(userId, role, courseId))
                return true;
        }

        return false;
    }
}
