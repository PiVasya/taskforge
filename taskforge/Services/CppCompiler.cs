using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CppCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public CppCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "cpp") { }
    }
}
