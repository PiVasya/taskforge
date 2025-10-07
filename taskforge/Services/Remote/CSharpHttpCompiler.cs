using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CSharpHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public CSharpHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "csharp")
        {
            _baseUrl = cfg["Compilers:csharp:Url"]
                    ?? cfg["Compilers__csharp__Url"]
                    ?? "http://csharp-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
