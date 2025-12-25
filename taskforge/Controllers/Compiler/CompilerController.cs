﻿using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CompilerController : ControllerBase
    {
        private readonly ICompilerService _svc;

        public CompilerController(ICompilerService svc)
        {
            _svc = svc;
        }

        [HttpPost("compile-run")]
        public async Task<IActionResult> CompileRun([FromBody] CompilerRunRequestDto req)
        {
            try
            {
                // Подстрахуем фронт: часто приходит "C++" из селекта.
                if (!string.IsNullOrWhiteSpace(req?.Language))
                {
                    var l = req.Language.Trim();
                    if (l.Equals("C++", StringComparison.OrdinalIgnoreCase)) req.Language = "cpp";
                    if (l.Equals("C#",  StringComparison.OrdinalIgnoreCase)) req.Language = "csharp";
                }

                var r = await _svc.CompileAndRunAsync(req);

                // Если раннер вернул compile_error, отдаём 400 (как было задумано в UI).
                if (string.Equals(r.Status, "compile_error", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(r);

                return Ok(r);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("run-tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto req)
        {
            try
            {
                req.Language ??= "cpp";
                var list = await _svc.RunTestsAsync(req);
                return Ok(new { results = list });
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
