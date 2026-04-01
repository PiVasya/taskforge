using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Services.AI;

public sealed class AiBootstrapService
{
    private readonly ApplicationDbContext _db;
    private readonly PasswordHasher _passwordHasher;
    private readonly IOptions<AiOptions> _options;
    private readonly ILogger<AiBootstrapService> _log;

    public AiBootstrapService(ApplicationDbContext db, PasswordHasher passwordHasher, IOptions<AiOptions> options, ILogger<AiBootstrapService> log)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options;
        _log = log;
    }

    public async Task EnsureSystemUserAsync(CancellationToken ct = default)
    {
        var opt = _options.Value;
        if (!opt.Enabled) return;

        var email = (opt.SystemUserEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email)) return;

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email.ToLower(), ct);
        if (user != null)
        {
            var changed = false;
            if (!string.Equals(user.Role, AppRoles.AiAgent, StringComparison.Ordinal))
            {
                user.Role = AppRoles.AiAgent;
                changed = true;
            }
            if (!string.Equals(user.FirstName, opt.SystemUserFirstName, StringComparison.Ordinal))
            {
                user.FirstName = opt.SystemUserFirstName;
                changed = true;
            }
            if (!string.Equals(user.LastName, opt.SystemUserLastName, StringComparison.Ordinal))
            {
                user.LastName = opt.SystemUserLastName;
                changed = true;
            }
            if (changed)
            {
                user.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("AI system user refreshed: {Email}", email);
            }
            return;
        }

        var password = $"ai-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        var (salt, hash) = _passwordHasher.HashPassword(password);
        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FirstName = opt.SystemUserFirstName,
            LastName = opt.SystemUserLastName,
            PasswordHash = hash,
            PasswordSalt = salt,
            EmailConfirmed = true,
            Role = AppRoles.AiAgent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("AI system user created: {Email}", email);
    }
}
