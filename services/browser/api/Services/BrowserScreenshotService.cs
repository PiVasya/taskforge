using System.Buffers.Binary;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed record BrowserScreenshot(
    byte[] Bytes,
    int Width,
    int Height,
    bool FullPage,
    bool FullPageTruncated,
    bool Annotated);

public sealed class BrowserScreenshotService(BrowserOptions options)
{
    private readonly BrowserOptions _options = options;

    public async Task<BrowserScreenshot> CaptureAsync(
        BrowserPageHandle handle,
        bool fullPage,
        bool annotated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentHeight = await handle.Page
            .EvaluateAsync<int>("() => Math.max(document.documentElement.scrollHeight, document.body?.scrollHeight || 0)")
            .WaitAsync(cancellationToken);
        var fullPagePixels = (long)handle.Width * System.Math.Max(1, documentHeight);
        var safeFullPage = fullPage
                           && documentHeight <= _options.MaxFullPageHeight
                           && fullPagePixels <= _options.MaxScreenshotPixels;

        if (annotated)
        {
            await handle.Page.EvaluateAsync(InstallOverlayScript).WaitAsync(cancellationToken);
        }

        try
        {
            var bytes = await handle.Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = safeFullPage,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Scale = ScreenshotScale.Css,
                Timeout = System.Math.Clamp(_options.ActionTimeoutSeconds, 1, 60) * 1000
            }).WaitAsync(cancellationToken);
            EnsureArtifactSize(bytes.Length);
            var (actualWidth, actualHeight) = ReadPngSize(bytes, handle.Width, safeFullPage ? documentHeight : handle.Height);
            return new BrowserScreenshot(bytes, actualWidth, actualHeight, safeFullPage, fullPage && !safeFullPage, annotated);
        }
        finally
        {
            if (annotated && !handle.Page.IsClosed)
            {
                try
                {
                    await handle.Page.EvaluateAsync("() => document.getElementById('__taskforge_agent_overlay__')?.remove()");
                }
                catch (PlaywrightException)
                {
                    // The page may have navigated while the screenshot was being completed.
                }
            }
        }
    }


    public void EnsureArtifactSize(int byteCount)
    {
        if (byteCount <= _options.MaxArtifactResponseBytes) return;
        throw new BrowserApiException(
            StatusCodes.Status413PayloadTooLarge,
            "RENDER_ARTIFACT_TOO_LARGE",
            $"Результат рендера превышает предел {_options.MaxArtifactResponseBytes} байт. Уменьшите viewport или отключите fullPage.");
    }

    public static (int Width, int Height) ReadPngSize(byte[] bytes, int fallbackWidth, int fallbackHeight)
    {
        if (bytes.Length >= 24
            && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
        {
            return (
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        }

        return (fallbackWidth, fallbackHeight);
    }

    private const string InstallOverlayScript = """
() => {
  document.getElementById('__taskforge_agent_overlay__')?.remove();
  document.querySelectorAll('[data-taskforge-agent-id]').forEach((element) => element.removeAttribute('data-taskforge-agent-id'));

  const visible = (element) => {
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.display !== 'none'
      && style.visibility !== 'hidden'
      && Number(style.opacity || '1') > 0
      && rect.width > 0
      && rect.height > 0;
  };

  const selectors = [
    'a[href]', 'button', 'input:not([type="hidden"])', 'select', 'textarea', 'summary',
    '[contenteditable="true"]', '[role="button"]', '[role="link"]', '[role="checkbox"]',
    '[role="radio"]', '[role="switch"]', '[role="tab"]', '[role="menuitem"]',
    '[role="option"]', '[tabindex]:not([tabindex="-1"])'
  ];

  const seen = new Set();
  const elements = [];
  document.querySelectorAll(selectors.join(',')).forEach((element) => {
    if (!seen.has(element) && visible(element)) {
      seen.add(element);
      elements.push(element);
    }
  });

  const overlay = document.createElement('div');
  overlay.id = '__taskforge_agent_overlay__';
  overlay.setAttribute('aria-hidden', 'true');
  Object.assign(overlay.style, {
    position: 'absolute',
    inset: '0',
    width: '0',
    height: '0',
    zIndex: '2147483647',
    pointerEvents: 'none'
  });

  elements.forEach((element, index) => {
    const id = `tf${index + 1}`;
    element.setAttribute('data-taskforge-agent-id', id);
    const rect = element.getBoundingClientRect();
    const label = document.createElement('span');
    label.textContent = id;
    Object.assign(label.style, {
      position: 'absolute',
      left: `${Math.max(0, rect.left + window.scrollX)}px`,
      top: `${Math.max(0, rect.top + window.scrollY - 15)}px`,
      padding: '1px 4px',
      border: '1px solid rgba(255,255,255,.95)',
      borderRadius: '3px',
      background: 'rgba(190, 0, 35, .96)',
      color: '#fff',
      font: '700 10px/12px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      boxShadow: '0 1px 3px rgba(0,0,0,.65)',
      whiteSpace: 'nowrap'
    });
    overlay.appendChild(label);
  });

  document.documentElement.appendChild(overlay);
  return elements.length;
}
""";
}
