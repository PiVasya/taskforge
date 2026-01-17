using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO.UserGroups
{
    public sealed class UserGroupDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string? Color { get; set; }
        public string? Icon { get; set; }
        public string? ExternalId { get; set; }
        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int MembersCount { get; set; }
    }

    public sealed class CreateUserGroupRequest
    {
        [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
        [Required, MaxLength(64)] public string Code { get; set; } = string.Empty;
        [MaxLength(2000)] public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        [MaxLength(50)] public string? Color { get; set; }
        [MaxLength(50)] public string? Icon { get; set; }
        [MaxLength(200)] public string? ExternalId { get; set; }
        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
    }

    public sealed class UpdateUserGroupRequest
    {
        [Required, MaxLength(200)] public string Name { get; set; } = string.Empty;
        [Required, MaxLength(64)] public string Code { get; set; } = string.Empty;
        [MaxLength(2000)] public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        [MaxLength(50)] public string? Color { get; set; }
        [MaxLength(50)] public string? Icon { get; set; }
        [MaxLength(200)] public string? ExternalId { get; set; }
        public string? Notes { get; set; }
        public string? TagsJson { get; set; }
    }

    public sealed class SetGroupMemberRequest
    {
        public Guid UserId { get; set; }
    }
}
