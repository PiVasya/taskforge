using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class PythonHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public PythonHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "python") { }
    }
}
