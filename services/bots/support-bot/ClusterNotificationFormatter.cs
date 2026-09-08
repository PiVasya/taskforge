using System.Net;

namespace TaskForge.SupportBot;

internal static class ClusterNotificationFormatter
{
    internal static string Format(ClusterEventNotification evt)
    {
        var kind = (evt.Kind ?? string.Empty).Trim();
        var node = (evt.Node ?? string.Empty).Trim();
        var message = Normalize(evt.Message);
        return kind switch
        {
            "cluster.primary_switch_requested" => Compose(
                "🔄", "Переключение Primary запущено",
                string.IsNullOrWhiteSpace(message)
                    ? NodeLine(node)
                    : $"{Encode(message)}\n\n<i>Дальше: Patroni → приложения → Cloudflare → HTTPS.</i>"),
            "cluster.leader_changed" => Compose(
                "🔁", "Primary кластера изменён",
                string.IsNullOrWhiteSpace(message) ? "Состояние Primary изменилось." : Encode(message)),
            "edge.public_ready" => Compose(
                "🌐", "Публичный маршрут готов",
                JoinLines(NodeLine(node), "Cloudflare, публичный маршрут и HTTPS подтверждены.")),
            "cluster.node_down" => Compose(
                "🔴", "Нода недоступна",
                JoinLines(NodeLine(node), string.IsNullOrWhiteSpace(message) ? "Node Agent перестал отвечать." : Encode(message))),
            "cluster.node_recovered" => Compose(
                "🟢", "Нода снова доступна",
                JoinLines(NodeLine(node), "Node Agent снова стабильно отвечает.")),
            "update.activated" => Compose(
                "🚀", "TaskForge обновлён",
                JoinLines(NodeLine(node), string.IsNullOrWhiteSpace(message) ? "Новая версия активирована." : Encode(message))),
            "update.watchtower_failed" => Compose(
                "⚠️", "Ошибка автообновления",
                JoinLines(NodeLine(node), string.IsNullOrWhiteSpace(message) ? "Watchtower сообщил об ошибке." : Encode(message))),
            "update.watchtower_recovered" => Compose(
                "🟢", "Автообновление восстановлено",
                JoinLines(NodeLine(node), string.IsNullOrWhiteSpace(message) ? "Watchtower снова работает." : Encode(message))),
            _ => Generic(evt, node, message),
        };
    }

    internal static string? SemanticKey(ClusterEventNotification evt)
    {
        var kind = (evt.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var node = (evt.Node ?? string.Empty).Trim().ToUpperInvariant();
        var message = Normalize(evt.Message).ToLowerInvariant();
        return kind switch
        {
            "cluster.leader_changed" => $"{kind}|{message}",
            "edge.public_ready" => $"{kind}|{node}",
            "cluster.primary_switch_requested" => $"{kind}|{message}",
            _ => null,
        };
    }

    private static string Generic(ClusterEventNotification evt, string node, string message)
    {
        var icon = (evt.Severity ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "error" => "🔴",
            "warning" => "⚠️",
            "success" => "🟢",
            _ => "ℹ️",
        };
        var title = string.IsNullOrWhiteSpace(evt.Title) ? "TaskForge cluster" : evt.Title.Trim();
        return Compose(icon, title, JoinLines(NodeLine(node), string.IsNullOrWhiteSpace(message) ? string.Empty : Encode(message)));
    }

    private static string Compose(string icon, string title, string body)
        => string.IsNullOrWhiteSpace(body)
            ? $"{icon} <b>{Encode(title)}</b>"
            : $"{icon} <b>{Encode(title)}</b>\n{body}";

    private static string NodeLine(string node)
        => string.IsNullOrWhiteSpace(node) ? string.Empty : $"Нода: <b>{Encode(node)}</b>";

    private static string JoinLines(params string[] parts)
        => string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Replace(" -> ", " → ", StringComparison.Ordinal);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
