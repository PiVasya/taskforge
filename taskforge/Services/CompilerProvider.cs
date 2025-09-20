using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public class CompilerProvider : ICompilerProvider
    {
        private readonly Dictionary<string, ICompiler> _compilers;

        public CompilerProvider(IEnumerable<ICompiler> compilers)
        {
            _compilers = compilers.ToDictionary(
                c => c.GetType().Name.Replace("Compiler", "").ToLowerInvariant(),
                c => c
            );
        }

        public ICompiler? GetCompiler(string language)
        {
            var key = language.ToLowerInvariant().Replace("c#", "csharp").Replace("c++", "cpp").Replace("python", "python");
            return _compilers.TryGetValue(key, out var compiler) ? compiler : null;
        }
    }
}
