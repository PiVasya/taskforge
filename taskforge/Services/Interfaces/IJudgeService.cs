using System;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO.Solutions;

namespace taskforge.Services.Interfaces
{
    public interface IJudgeService
    {
        Task<JudgeResponseDto> JudgeAsync(JudgeRequestDto req, Guid currentUserId);
    }
}
