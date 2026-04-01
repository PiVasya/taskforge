using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiUserRiskReport
{
    [Key]
    public Guid Id { get; set; }

    public Guid? JobId { get; set; }
    public AiJob? Job { get; set; }

    [Required]
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required, MaxLength(32)]
    public string RiskLevel { get; set; } = "low";

    public double Score { get; set; } = 0;

    [Required]
    public string Summary { get; set; } = string.Empty;

    public string? SignalsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
}
