using System.Text.Json;
using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Services;

public sealed class SnapshotBuilder(
    BrowserOptions options,
    ILogger<SnapshotBuilder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly BrowserOptions _options = options;
    private readonly ILogger<SnapshotBuilder> _logger = logger;

    public async Task<SiteSnapshotResponse> BuildAsync(
        BrowserPageHandle handle,
        bool includeText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await handle.Page.EvaluateAsync<string>(SnapshotScript).WaitAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<SnapshotDomPayload>(json, JsonOptions) ?? new SnapshotDomPayload();

        var totalElements = payload.Elements.Count;
        var elementsTruncated = totalElements > _options.MaxSnapshotElements;
        var elements = payload.Elements.Take(System.Math.Max(1, _options.MaxSnapshotElements)).ToList();

        var text = includeText ? payload.Text ?? string.Empty : string.Empty;
        var textTruncated = text.Length > _options.MaxSnapshotTextCharacters;
        if (textTruncated) text = text[..System.Math.Max(1, _options.MaxSnapshotTextCharacters)];

        var ariaSnapshot = await BuildAriaSnapshotAsync(handle.Page, cancellationToken);
        var ariaSnapshotTruncated = ariaSnapshot.Length > _options.MaxAriaSnapshotCharacters;
        if (ariaSnapshotTruncated)
        {
            ariaSnapshot = ariaSnapshot[..System.Math.Max(1, _options.MaxAriaSnapshotCharacters)];
        }

        var allowedIds = elements.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        payload.Issues.SmallTouchTargetElementIds = payload.Issues.SmallTouchTargetElementIds.Where(allowedIds.Contains).ToList();
        payload.Issues.TouchTargetBelow44ElementIds = payload.Issues.TouchTargetBelow44ElementIds.Where(allowedIds.Contains).ToList();
        payload.Issues.UnlabelledInteractiveElementIds = payload.Issues.UnlabelledInteractiveElementIds.Where(allowedIds.Contains).ToList();

        return new SiteSnapshotResponse
        {
            Site = handle.Site,
            Url = SafePageUrl(handle.Page.Url),
            Title = await handle.Page.TitleAsync().WaitAsync(cancellationToken),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Authenticated = handle.Caller.IsAuthenticated,
            AccountType = handle.Caller.IsAuthenticated ? handle.Caller.AccountType : "anonymous",
            ReadOnly = handle.ReadOnly,
            Viewport = payload.Viewport,
            Document = payload.Document,
            Text = text,
            AriaSnapshot = ariaSnapshot,
            Headings = payload.Headings.Take(100).ToList(),
            Elements = elements,
            Issues = payload.Issues,
            Performance = payload.Performance,
            Console = handle.Events.Console(),
            NetworkFailures = handle.Events.NetworkFailures(),
            HttpErrors = handle.Events.HttpErrors(),
            Truncation = new SnapshotTruncation
            {
                TextTruncated = textTruncated,
                ElementsTruncated = elementsTruncated,
                AriaSnapshotTruncated = ariaSnapshotTruncated,
                TotalInteractiveElements = totalElements
            }
        };
    }

    private async Task<string> BuildAriaSnapshotAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.AriaSnapshotAsync(new PageAriaSnapshotOptions
            {
                Mode = AriaSnapshotMode.Ai,
                Boxes = true,
                Depth = System.Math.Clamp(_options.AriaSnapshotDepth, 1, 50),
                Timeout = System.Math.Clamp(_options.ActionTimeoutSeconds, 1, 60) * 1000
            }).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogDebug(ex, "Playwright ARIA snapshot was unavailable for {Url}.", SafePageUrl(page.Url));
            return string.Empty;
        }
    }

    private static string SafePageUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        return uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrWhiteSpace(uri.Query) ? string.Empty : "?[query-redacted]");
    }

    private sealed class SnapshotDomPayload
    {
        public SnapshotViewport Viewport { get; set; } = new();
        public SnapshotDocument Document { get; set; } = new();
        public string? Text { get; set; }
        public List<SnapshotHeading> Headings { get; set; } = [];
        public List<SnapshotElement> Elements { get; set; } = [];
        public SnapshotIssues Issues { get; set; } = new();
        public SnapshotPerformance Performance { get; set; } = new();
    }

    private const string SnapshotScript = """
() => {
  const cleanText = (value, max = 600) => String(value || '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, max);

  const finite = (value) => Number.isFinite(Number(value)) ? Number(Number(value).toFixed(2)) : null;

  const rectOf = (rect) => ({
    x: Number((rect.x + window.scrollX).toFixed(2)),
    y: Number((rect.y + window.scrollY).toFixed(2)),
    width: Number(rect.width.toFixed(2)),
    height: Number(rect.height.toFixed(2))
  });

  const visible = (element) => {
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.display !== 'none'
      && style.visibility !== 'hidden'
      && Number(style.opacity || '1') > 0
      && rect.width > 0
      && rect.height > 0;
  };

  const labelledBy = (element) => {
    const value = element.getAttribute('aria-labelledby');
    if (!value) return '';
    return cleanText(value.split(/\s+/).map((id) => document.getElementById(id)?.textContent || '').join(' '));
  };

  const nativeLabels = (element) => {
    if (!('labels' in element) || !element.labels) return '';
    return cleanText(Array.from(element.labels).map((label) => label.innerText || label.textContent || '').join(' '));
  };

  const inputButtonValue = (element) => {
    if (!(element instanceof HTMLInputElement)) return '';
    const type = String(element.type || 'text').toLowerCase();
    return ['button', 'submit', 'reset', 'image'].includes(type) ? cleanText(element.value) : '';
  };

  const semanticName = (element) => cleanText(
    element.getAttribute('aria-label')
      || labelledBy(element)
      || nativeLabels(element)
      || element.getAttribute('alt')
      || element.getAttribute('title')
      || inputButtonValue(element)
      || element.innerText
      || element.textContent
  );

  const agentName = (element) => semanticName(element)
    || cleanText(element.getAttribute('placeholder'));

  const inferredRole = (element) => {
    const explicit = element.getAttribute('role');
    if (explicit) return explicit;
    const tag = element.tagName.toLowerCase();
    if (tag === 'a') return 'link';
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'select') return 'combobox';
    if (tag === 'textarea' || element.getAttribute('contenteditable') === 'true') return 'textbox';
    if (tag === 'input') {
      const type = String(element.getAttribute('type') || 'text').toLowerCase();
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'range') return 'slider';
      if (['button', 'submit', 'reset', 'image'].includes(type)) return 'button';
      return 'textbox';
    }
    return 'interactive';
  };

  document.querySelectorAll('[data-taskforge-agent-id]').forEach((el) => el.removeAttribute('data-taskforge-agent-id'));

  const selectors = [
    'a[href]',
    'button',
    'input:not([type="hidden"])',
    'select',
    'textarea',
    'summary',
    '[contenteditable="true"]',
    '[role="button"]',
    '[role="link"]',
    '[role="checkbox"]',
    '[role="radio"]',
    '[role="switch"]',
    '[role="tab"]',
    '[role="menuitem"]',
    '[role="option"]',
    '[tabindex]:not([tabindex="-1"])'
  ];

  const unique = [];
  const seen = new Set();
  document.querySelectorAll(selectors.join(',')).forEach((element) => {
    if (!seen.has(element) && visible(element)) {
      seen.add(element);
      unique.push(element);
    }
  });

  const elements = unique.map((element, index) => {
    const id = `tf${index + 1}`;
    element.setAttribute('data-taskforge-agent-id', id);
    const rect = element.getBoundingClientRect();
    const tag = element.tagName.toLowerCase();
    const type = element.getAttribute('type');
    const href = tag === 'a' && element.href
      ? (() => {
          try {
            const u = new URL(element.href, location.href);
            return u.origin === location.origin
              ? `${u.pathname}${u.search ? '?[query-redacted]' : ''}${u.hash ? '#[fragment-redacted]' : ''}`
              : `${u.origin}${u.pathname}`;
          } catch {
            return null;
          }
        })()
      : null;

    return {
      id,
      tag,
      role: inferredRole(element),
      name: agentName(element),
      text: cleanText(element.innerText || element.textContent, 800),
      type,
      href,
      placeholder: element.getAttribute('placeholder'),
      testId: element.getAttribute('data-testid'),
      disabled: Boolean(element.disabled || element.getAttribute('aria-disabled') === 'true'),
      checked: Boolean(element.checked || element.getAttribute('aria-checked') === 'true'),
      selected: Boolean(element.selected || element.getAttribute('aria-selected') === 'true'),
      inViewport: rect.bottom > 0 && rect.right > 0 && rect.top < window.innerHeight && rect.left < window.innerWidth,
      bounds: rectOf(rect),
      _semanticName: semanticName(element)
    };
  });

  const smallTargets = elements.filter((x) => x.bounds.width < 24 || x.bounds.height < 24);
  const touchTargetsBelow44 = elements.filter((x) => x.bounds.width < 44 || x.bounds.height < 44);
  const unlabelled = elements.filter((x) => !x._semanticName);
  elements.forEach((element) => delete element._semanticName);

  const images = Array.from(document.images);
  const imagesWithoutAlt = images.filter((img) => !img.hasAttribute('alt'));

  const idCounts = new Map();
  document.querySelectorAll('[id]').forEach((el) => idCounts.set(el.id, (idCounts.get(el.id) || 0) + 1));
  const duplicateIdCount = Array.from(idCounts.values())
    .filter((count) => count > 1)
    .reduce((sum, count) => sum + count - 1, 0);

  const documentWidth = Math.max(document.documentElement.scrollWidth, document.body?.scrollWidth || 0);
  const documentHeight = Math.max(document.documentElement.scrollHeight, document.body?.scrollHeight || 0);
  const overflowElements = [];
  if (documentWidth > document.documentElement.clientWidth + 1) {
    document.querySelectorAll('body *').forEach((element) => {
      if (overflowElements.length >= 50 || !visible(element)) return;
      const rect = element.getBoundingClientRect();
      if (rect.right > document.documentElement.clientWidth + 1 || rect.left < -1) {
        overflowElements.push({
          tag: element.tagName.toLowerCase(),
          id: cleanText(element.id, 120),
          className: cleanText(typeof element.className === 'string' ? element.className : '', 240),
          bounds: rectOf(rect)
        });
      }
    });
  }

  const headings = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6'))
    .filter(visible)
    .slice(0, 200)
    .map((heading) => ({
      level: Number(heading.tagName.slice(1)),
      text: cleanText(heading.innerText || heading.textContent, 800)
    }));

  let headingLevelSkipCount = 0;
  for (let index = 1; index < headings.length; index += 1) {
    if (headings[index].level > headings[index - 1].level + 1) headingLevelSkipCount += 1;
  }

  const nav = performance.getEntriesByType('navigation')[0];
  const paints = performance.getEntriesByType('paint');
  const fcp = paints.find((entry) => entry.name === 'first-contentful-paint');
  const resources = performance.getEntriesByType('resource');
  const observed = window.__TASKFORGE_BROWSER_METRICS__ || {};

  const result = {
    viewport: {
      width: window.innerWidth,
      height: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
      scrollX: window.scrollX,
      scrollY: window.scrollY
    },
    document: {
      width: documentWidth,
      height: documentHeight,
      horizontalOverflow: documentWidth > document.documentElement.clientWidth + 1,
      language: document.documentElement.lang || '',
      interactiveElementCount: elements.length,
      imageCount: images.length
    },
    text: document.body?.innerText || '',
    headings,
    elements,
    issues: {
      smallTouchTargetCount: smallTargets.length,
      smallTouchTargetElementIds: smallTargets.slice(0, 100).map((x) => x.id),
      touchTargetBelow44Count: touchTargetsBelow44.length,
      touchTargetBelow44ElementIds: touchTargetsBelow44.slice(0, 100).map((x) => x.id),
      unlabelledInteractiveCount: unlabelled.length,
      unlabelledInteractiveElementIds: unlabelled.slice(0, 100).map((x) => x.id),
      imagesWithoutAltCount: imagesWithoutAlt.length,
      duplicateIdCount,
      h1Count: headings.filter((heading) => heading.level === 1).length,
      headingLevelSkipCount,
      missingDocumentLanguage: !document.documentElement.lang,
      overflowElements
    },
    performance: {
      domContentLoadedMilliseconds: finite(nav?.domContentLoadedEventEnd),
      loadMilliseconds: finite(nav?.loadEventEnd),
      firstContentfulPaintMilliseconds: finite(fcp?.startTime),
      largestContentfulPaintMilliseconds: finite(observed.largestContentfulPaint),
      cumulativeLayoutShift: finite(observed.cumulativeLayoutShift),
      resourceCount: resources.length,
      transferSizeBytes: resources.reduce((sum, entry) => sum + Number(entry.transferSize || 0), 0),
      longTaskCount: Number(observed.longTaskCount || 0),
      longTaskDurationMilliseconds: finite(observed.longTaskDuration) || 0
    }
  };

  return JSON.stringify(result);
}
""";
}
