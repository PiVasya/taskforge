using taskforge.Data.Models.DTO.Support;

namespace taskforge.Services.Interfaces
{
    public interface ISupportService
    {
        Task<IReadOnlyList<SupportTicketListItemDto>> GetTicketsAsync(Guid userId, bool isSupportAdmin, CancellationToken ct);
        Task<SupportTicketDetailsDto?> GetTicketAsync(Guid ticketId, Guid userId, bool isSupportAdmin, CancellationToken ct);
        Task<Guid> CreateTicketAsync(Guid userId, string? type, string message, CancellationToken ct);
        Task<Guid> AddMessageAsync(Guid ticketId, Guid userId, bool isSupportAdmin, string? adminName, string message, CancellationToken ct);
        Task CloseTicketAsync(Guid ticketId, bool isSupportAdmin, CancellationToken ct);

        /// <summary>
        /// Внешнее сообщение от админа (Telegram/иные каналы). Записывается как админское и
        /// транслируется через SignalR.
        /// </summary>
        Task AddExternalAdminMessageAsync(Guid ticketId, string authorName, string message, string source, string? externalMessageId, CancellationToken ct);
    }
}
