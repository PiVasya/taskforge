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
        new("main", "/admin/system-status", "Состояние системы", true, "admin", "Admin"),
        new("main", "/admin/ai", "AI-раздел", true, "redirect", "Admin", "Перенаправляет на /admin/ai/account-manager."),
        new("main", "/admin/ai/account-manager", "AI Account Manager", true, "admin", "Admin"),
        new("main", "/admin/ai/assistant", "AI-ассистент", true, "admin", "Admin"),
        new("main", "/admin/analytics", "Аналитика", true, "admin", "Admin"),
        new("main", "/admin/activity", "Действия пользователей", true, "admin", "Admin"),
        new("main", "/admin/users", "Пользователи", true, "admin", "Admin"),
        new("main", "/admin/users/:userId", "Управление пользователем", true, "admin", "Admin"),
        new("main", "/admin/minecraft-links", "Minecraft-привязки", true, "admin", "Admin"),
        new("main", "/admin/assignments/:assignmentId/insights", "Аналитика задания", true, "admin", "Admin"),
        new("main", "/agent", "AI-ассистент", true, "redirect", "Admin", "Перенаправляет в административный AI-раздел."),
        new("main", "/ai", "AI-ассистент", true, "redirect", "Admin", "Перенаправляет в административный AI-раздел."),

        // CT frontend.
        new("ct", "/login", "Вход CT", false, "auth"),
        new("ct", "/register", "Регистрация CT", false, "auth", Notes: "Добавьте ?accountType=ai для регистрации AI-аккаунта через интерфейс."),
        new("ct", "/privacy", "Политика конфиденциальности CT", false, "document"),
        new("ct", "/", "Главная CT", true),
        new("ct", "/courses", "Курсы CT", true),
        new("ct", "/courses/:courseSlug", "Курс CT", true, "dynamic"),
        new("ct", "/courses/:courseSlug/conspects/:slug", "Конспект CT", true, "dynamic"),
        new("ct", "/courses/:courseSlug/tasks", "Задания курса CT", true, "dynamic"),
        new("ct", "/tasks", "Задания CT", true, "redirect", Notes: "Перенаправляет на главную CT."),
        new("ct", "/conspects/:slug", "Конспект CT", true, "dynamic"),
        new("ct", "/editor", "Редактор CT", true, "editor", Notes: "Маршрут перенаправляет в редактор доступного раздела."),
        new("ct", "/editor/:sectionCode", "Редактор раздела CT", true, "editor"),
        new("ct", "/editor/courses/:courseSlug", "Редактор курса CT", true, "editor"),
        new("ct", "/:sectionCode", "Раздел CT", true, "dynamic"),
        new("ct", "/admin/conspects", "Администрирование конспектов CT", true, "admin", "Admin")
    };

    public IReadOnlyList<SiteRouteDto> GetRoutes(string? site = null)
        => string.IsNullOrWhiteSpace(site)
            ? Routes
            : Routes.Where(route => string.Equals(route.Site, site, StringComparison.OrdinalIgnoreCase)).ToArray();
}
