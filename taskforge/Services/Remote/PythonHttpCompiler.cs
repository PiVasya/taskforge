using Microsoft.Extensions.Configuration;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    public sealed class PythonHttpCompiler : HttpRunnerCompilerBase, ICompiler
    {
        private readonly string _baseUrl;

        public PythonHttpCompiler(IHttpClientFactory http, IConfiguration cfg)
            : base(http, cfg, "python")
        {
            _baseUrl = cfg["Compilers:python:Url"]
                    ?? cfg["Compilers__python__Url"]
                    ?? "http://python-runner:8080";
        }

        protected override string BaseUrl => _baseUrl;
    }
}
