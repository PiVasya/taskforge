using System.Threading;
using System.Threading.Tasks;

namespace taskforge.Services.Integrations;

public interface IMinecraftServerNotifier
{
    /// <summary>
    /// Попросить Minecraft-сервер доставить игроку (nick) одноразовый код.
    /// Возвращает (ok, message).
    /// </summary>
    Task<(bool Ok, string Message)> SendLinkCodeAsync(string nick, string code, CancellationToken ct);
}
