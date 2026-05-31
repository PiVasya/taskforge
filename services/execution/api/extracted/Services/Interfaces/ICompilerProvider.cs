namespace taskforge.Services.Interfaces
{
    public interface ICompilerProvider
    {
        ICompiler? GetCompiler(string language);
    }
}
