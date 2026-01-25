using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using taskforge.Services.Files;

namespace taskforge.Controllers.Files;

[ApiController]
[Route("api")]
[Authorize]
public sealed class FilesController : ControllerBase
{
    private readonly IFileStorageService _store;

    public FilesController(IFileStorageService store)
    {
        _store = store;
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
}
