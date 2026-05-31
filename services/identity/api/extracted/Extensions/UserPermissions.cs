using System.Security.Claims;

namespace taskforge.Extensions
{
    public static class UserPermissions
    {
        /// <summary>
        /// В проекте админские/редакторские возможности исторически проверяются по:
        /// - роли Admin
        /// - роли Instructor
        /// - claim canEdit=true
        /// Держим логику в одном месте.
        /// </summary>
        public static bool CanEdit(ClaimsPrincipal user)
        {
            return user.IsInRole("Admin")
                   || user.IsInRole("Instructor")
                   || user.HasClaim("canEdit", "true");
        }

        /// <summary>
        /// Доступ к списку всех обращений/закрытию тикетов.
        /// Сейчас совпадает с CanEdit.
        /// </summary>
        public static bool IsSupportAdmin(ClaimsPrincipal user) => CanEdit(user);
    }
}
