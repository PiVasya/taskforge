namespace TaskForge.Minecraft.Api.Contracts;

public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string[]? Roles { get; set; }
    public string[]? FeatureRoles { get; set; }

    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Login;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}

public sealed class UserActivitySummaryDto
{
    public int SolvedAssignments { get; set; }
    public int TotalAttempts { get; set; }
    public int CodeSolutions { get; set; }
    public int ImageSolutions { get; set; }
    public int TestAttempts { get; set; }
    public int MathAttempts { get; set; }
    public int Score { get; set; }
    public int Rating { get; set; }
}
