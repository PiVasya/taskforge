using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Services;

public sealed class SiteRouteCatalog
{
    private static readonly IReadOnlyList<SiteRouteDto> Routes = new List<SiteRouteDto>
    {
        // Main TaskForge frontend.
        new("main", "/", "Главная", false),
        new("main", "/login", "Вход", false, "auth"),
        new("main", "/forgot-password", "Восстановление пароля", false, "auth"),
        new("main", "/register", "Регистрация", false, "auth", Notes: "Добавьте ?accountType=ai для регистрации AI-аккаунта через интерфейс."),
        new("main", "/privacy", "Политика конфиденциальности", false, "document"),
        new("main", "/technology", "Технический обзор", false, "hidden"),
        new("main", "/news", "Новости", false),
        new("main", "/news/:postId", "Новость", false, "dynamic"),

        new("main", "/courses", "Курсы", true),
        new("main", "/compiler", "Компилятор", true),
        new("main", "/course/:courseId", "Задания курса", true, "dynamic"),
        new("main", "/assignment/:assignmentId", "Решение задания", true, "dynamic"),
        new("main", "/assignment/:assignmentId/database", "База данных SQL-задания", true, "dynamic"),
        new("main", "/assignment/:assignmentId/results", "Результаты задания", true, "dynamic"),
        new("main", "/assignment/:assignmentId/image-results", "Результаты задания с изображением", true, "dynamic"),
        new("main", "/assignment/:assignmentId/top", "Лучшие решения задания", true, "dynamic"),
        new("main", "/profile", "Профиль", true),
        new("main", "/settings", "Настройки", true),
        new("main", "/my/solutions", "Мои решения", true),
        new("main", "/leaderboard", "Рейтинг", true),
        new("main", "/users/:userId", "Публичный профиль", true, "dynamic"),
        new("main", "/support", "Поддержка", true),
        new("main", "/support/new", "Новый диалог поддержки", true, "redirect", Notes: "Перенаправляет на /support."),
        new("main", "/support/:ticketId", "Диалог поддержки", true, "dynamic"),
        new("main", "/minecraft/chat", "Minecraft-чат", true, "feature", "Minecraft"),
        new("main", "/courses/:courseId/edit", "Редактор курса", true, "editor", Notes: "Требуются права редактора курса."),
        new("main", "/assignment/:assignmentId/edit", "Редактор задания", true, "editor", Notes: "Требуются права редактора курса."),

        new("main", "/admin/solutions", "Администрирование решений", true, "admin", "Admin"),
        new("main", "/admin/badges", "Администрирование достижений", true, "admin", "Admin"),
        new("main", "/admin/support", "Администрирование поддержки", true, "admin", "Admin"),
        new("main", "/admin/support/:ticketId", "Административный диалог поддержки", true, "admin", "Admin"),
        new("main", "/admin/groups", "Администрирование групп", true, "admin", "Admin"),
        new("main", "/admin/feature-roles", "Функциональные роли", true, "admin", "Admin"),
        new("main", "/admin/cluster", "Кластер", true, "admin", "Admin"),
        new("main", "/admin/system-status", "Состояние системы", true, "redirect", "Admin", "Перенаправляет на /admin/cluster."),
        new("main", "/admin/ai", "Менеджер аккаунтов", true, "redirect", "Admin", "Перенаправляет на /admin/ai/account-manager."),
        new("main", "/admin/ai/account-manager", "Менеджер аккаунтов", true, "admin", "Admin"),
        new("main", "/admin/analytics", "Аналитика", true, "admin", "Admin"),
        new("main", "/admin/activity", "Действия пользователей", true, "admin", "Admin"),
        new("main", "/admin/users", "Пользователи", true, "admin", "Admin"),
        new("main", "/admin/users/:userId", "Управление пользователем", true, "admin", "Admin"),
        new("main", "/admin/minecraft-links", "Minecraft-привязки", true, "admin", "Admin"),
        new("main", "/admin/assignments/:assignmentId/insights", "Аналитика задания", true, "admin", "Admin"),
    };

    public IReadOnlyList<SiteRouteDto> GetRoutes(string? site = null)
        => string.IsNullOrWhiteSpace(site)
            ? Routes
            : Routes.Where(route => string.Equals(route.Site, site, StringComparison.OrdinalIgnoreCase)).ToArray();
}
