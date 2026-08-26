namespace TaskForge.SupportBot;

public sealed record ClusterEventNotification(
    string? Id,
    string? At,
    string? Node,
    string? Kind,
    string? Severity,
    string? Title,
    string? Message);
