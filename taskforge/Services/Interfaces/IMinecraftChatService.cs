using taskforge.Data.Models.Entities;

namespace taskforge.Services.Interfaces
{
    public interface IMinecraftChatService
    {
        Task<IReadOnlyList<MinecraftChatMessage>> GetRecentAsync(int take, CancellationToken ct = default);
        Task<IReadOnlyList<MinecraftChatMessage>> GetOutgoingForMinecraftAsync(DateTime? afterUtc, int take, CancellationToken ct = default);
        Task<MinecraftChatMessage> AddSiteMessageAsync(Guid userId, bool isAdmin, string message, CancellationToken ct = default);
        Task<MinecraftChatMessage> AddMinecraftMessageAsync(string nick, string? uuid, string message, CancellationToken ct = default);
    }
}
