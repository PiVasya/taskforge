namespace taskforge.Services.Interfaces
{
    public interface ICurrentUserService
    {
        Guid GetUserId(); // бросает если нет/невалидно
    }
}
