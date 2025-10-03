using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CSharpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public CSharpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "csharp") { }
    }
}
