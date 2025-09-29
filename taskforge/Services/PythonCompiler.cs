using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class PythonCompiler : HttpRunnerCompilerBase, ICompiler
    {
        public PythonCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "python") { }
    }
}
