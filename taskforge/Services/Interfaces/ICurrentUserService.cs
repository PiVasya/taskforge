﻿namespace taskforge.Services.Interfaces
{
    public interface ICurrentUserService
    {
        Guid GetUserId();            // бросает если нет/невалидно
        string? GetRole();           // null, если нет клейма
        bool IsAdmin();              // роль == Admin
        bool IsEditor();             // роль == Editor
        bool IsAdminOrEditor();      // Admin || Editor
    }
}
