using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    /// <summary>
    /// HTTP‑компилятор для JavaScript. Использует javascript-runner.
    /// </summary>
    public sealed class JavascriptHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public JavascriptHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "javascript")
        {
            _baseUrl = cfg["Compilers:javascript:Url"]
                    ?? cfg["Compilers__javascript__Url"]
                    ?? "http://javascript-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
