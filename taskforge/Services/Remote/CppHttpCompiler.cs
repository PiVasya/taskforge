using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CppHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public CppHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "cpp") { }
    }
}
