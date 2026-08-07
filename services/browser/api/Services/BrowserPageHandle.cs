using Microsoft.Playwright;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Services;

public sealed class BrowserPageHandle : IAsyncDisposable
{
    public required string Site { get; init; }
    public required Uri SiteBaseUri { get; init; }
    public required BrowserCaller Caller { get; init; }
    public required bool ReadOnly { get; init; }
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; init; }
    public required BrowserEventBuffer Events { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? LastSafeUrl { get; set; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Context.CloseAsync();
        }
        catch
        {
            // Closing a context is best-effort during cleanup.
        }
    }
}
