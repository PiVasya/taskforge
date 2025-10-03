using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CSharpHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public CSharpHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "csharp") { }
    }
}
