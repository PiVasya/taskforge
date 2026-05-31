using System;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO.Solutions;

namespace taskforge.Services.Interfaces;

/// <summary>
/// Judge pipeline v2: before running tests, validates source code via external code-analyzer.
/// Old IJudgeService remains intact for backward compatibility.
/// </summary>
public interface IPolicyJudgeService
{
    Task<JudgeResponseDto> JudgeAsync(JudgeRequestDto req, Guid currentUserId);
}
