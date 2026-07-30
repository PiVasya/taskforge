namespace TaskForge.SupportBot;

public sealed record PasswordRecoveryDeliveryRequest(
    long TelegramChatId,
    string VerificationCode,
    string? AccountName,
    string? Login,
    int LifetimeMinutes);

public sealed record PasswordRecoveryDeliveryResult(
    bool Delivered,
    string Code,
    string Message)
{
    public static PasswordRecoveryDeliveryResult Success { get; } =
        new(true, "DELIVERED", "Код восстановления отправлен.");

    public static PasswordRecoveryDeliveryResult TelegramNotReady { get; } =
        new(false, "TELEGRAM_NOT_READY", "Telegram-бот временно не готов к отправке сообщений.");

    public static PasswordRecoveryDeliveryResult DeliveryFailed { get; } =
        new(false, "TELEGRAM_DELIVERY_FAILED", "Telegram не принял сообщение с кодом восстановления.");
}
