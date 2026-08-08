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

  const area = (rect) => Math.max(0, rect.right - rect.left) * Math.max(0, rect.bottom - rect.top);
  const intersect = (a, b) => ({
    left: Math.max(a.left, b.left),
    top: Math.max(a.top, b.top),
    right: Math.min(a.right, b.right),
    bottom: Math.min(a.bottom, b.bottom)
  });
  const clipsAxis = (value) => ['hidden', 'clip', 'auto', 'scroll'].includes(String(value || '').toLowerCase());

  const visibilityOf = (element) => {
    if (element.closest('[hidden], [inert], [aria-hidden="true"]')) return null;
    const details = element.closest('details:not([open])');
    if (details) {
      const summary = details.querySelector(':scope > summary');
      if (!summary || !summary.contains(element)) return null;
    }

    try {
      if (typeof element.checkVisibility === 'function'
          && !element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) return null;
    } catch {}

    const source = element.getBoundingClientRect();
    if (source.width <= 0 || source.height <= 0) return null;
    let visibleRect = { left: source.left, top: source.top, right: source.right, bottom: source.bottom };
    let ancestor = element;
    while (ancestor && ancestor instanceof Element) {
      const style = window.getComputedStyle(ancestor);
      if (style.display === 'none'
          || style.visibility === 'hidden'
          || style.visibility === 'collapse'
          || Number(style.opacity || '1') <= 0) return null;
      if (ancestor !== element && (clipsAxis(style.overflowX) || clipsAxis(style.overflowY))) {
        const clip = ancestor.getBoundingClientRect();
        visibleRect = intersect(visibleRect, {
          left: clipsAxis(style.overflowX) ? clip.left : -Infinity,
          right: clipsAxis(style.overflowX) ? clip.right : Infinity,
          top: clipsAxis(style.overflowY) ? clip.top : -Infinity,
          bottom: clipsAxis(style.overflowY) ? clip.bottom : Infinity
        });
        if (area(visibleRect) <= 0) return null;
      }
      ancestor = ancestor.parentElement;
    }
    return { element, visibleRect };
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
    if (seen.has(element)) return;
    const visibility = visibilityOf(element);
    if (!visibility) return;
    seen.add(element);
    elements.push(visibility);
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

  elements.forEach(({ element, visibleRect }, index) => {
    const id = `tf${index + 1}`;
    element.setAttribute('data-taskforge-agent-id', id);
    const label = document.createElement('span');
    label.textContent = id;
    Object.assign(label.style, {
      position: 'absolute',
      left: `${Math.max(0, visibleRect.left + window.scrollX)}px`,
      top: `${Math.max(0, visibleRect.top + window.scrollY - 15)}px`,
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
