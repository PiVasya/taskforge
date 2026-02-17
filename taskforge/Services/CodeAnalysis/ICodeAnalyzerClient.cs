using System.Threading;
using System.Threading.Tasks;
using taskforge.Services.CodeAnalysis.Models;

namespace taskforge.Services.CodeAnalysis;

public interface ICodeAnalyzerClient
{
    Task<AnalyzeResponse> AnalyzeAsync(AnalyzeRequest req, CancellationToken ct = default);
}
