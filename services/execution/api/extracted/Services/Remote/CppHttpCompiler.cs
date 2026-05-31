using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class CppHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public CppHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "cpp")
        {
            _baseUrl = cfg["Compilers:cpp:Url"]
                    ?? cfg["Compilers__cpp__Url"]
                    ?? "http://cpp-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
