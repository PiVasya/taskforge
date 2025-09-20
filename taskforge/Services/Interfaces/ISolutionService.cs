using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ISolutionService
    {
        Task<SubmitSolutionResponse> SubmitAsync(Guid assignmentId, Guid userId, SubmitSolutionRequest req);
    }
}
