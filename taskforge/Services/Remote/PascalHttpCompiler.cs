using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    /// <summary>
    /// HTTP‑компилятор для Pascal. Использует pascal-runner.
    /// </summary>
    public sealed class PascalHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public PascalHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "pascal")
        {
            _baseUrl = cfg["Compilers:pascal:Url"]
                    ?? cfg["Compilers__pascal__Url"]
                    ?? "http://pascal-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
