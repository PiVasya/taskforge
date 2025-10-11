namespace taskforge.Data.Models.DTO;

public sealed class SolutionListItemDto
{
    public Guid Id { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string CourseTitle { get; set; } = "";
    public string AssignmentTitle { get; set; } = "";
    public string Language { get; set; } = "";
    public bool PassedAllTests { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
}

public sealed class SolutionDetailsDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TaskAssignmentId { get; set; }
    public string SubmittedCode { get; set; } = "";
    public string Language { get; set; } = "";
    public bool PassedAllTests { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string CourseTitle { get; set; } = "";
    public string AssignmentTitle { get; set; } = "";
    public IList<SolutionCaseResultDto>? Cases { get; set; }
}

public sealed class LeaderboardEntryDto
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public int Solved { get; set; }
}
