using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    /// <summary>
    /// HTTP‑компилятор для Java. Использует java-runner.
    /// </summary>
    public sealed class JavaHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public JavaHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "java")
        {
            _baseUrl = cfg["Compilers:java:Url"]
                    ?? cfg["Compilers__java__Url"]
                    ?? "http://java-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
