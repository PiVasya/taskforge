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

        var policyBlocked = handle.Events.PolicyBlockedRequests();
        var readiness = handle.Readiness;
        readiness.PendingRequestCount = handle.Events.PendingRequestCount;
        readiness.PendingRequests = handle.Events.PendingRequests();

        return new SiteSnapshotResponse
        {
            SemanticSnapshotVersion = "2.1",
            Site = handle.Site,
            Url = SafePageUrl(handle.Page.Url),
            Title = await handle.Page.TitleAsync().WaitAsync(cancellationToken),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Authenticated = handle.Caller.IsAuthenticated,
            AccountType = handle.Caller.IsAuthenticated ? handle.Caller.AccountType : "anonymous",
            ReadOnly = handle.ReadOnly,
            CaptureMode = handle.Caller.IsAuthenticated
                ? handle.ReadOnly ? "authenticated-read-only" : "authenticated-interactive"
                : "anonymous-read-only",
            PolicyInterference = policyBlocked.Count > 0,
            PageReadyState = readiness.PageReadyState,
            AppReady = readiness.AppReady,
            Readiness = readiness,
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
            PolicyBlockedRequests = policyBlocked,
            Truncation = new SnapshotTruncation
            {
                TextTruncated = textTruncated,
                ElementsTruncated = elementsTruncated,
                AriaSnapshotTruncated = ariaSnapshotTruncated,
                TotalInteractiveElements = payload.Document.InteractiveElementCount
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
  const round = (value) => Number(Number(value || 0).toFixed(2));
  const area = (rect) => Math.max(0, rect.right - rect.left) * Math.max(0, rect.bottom - rect.top);
  const intersect = (a, b) => ({
    left: Math.max(a.left, b.left),
    top: Math.max(a.top, b.top),
    right: Math.min(a.right, b.right),
    bottom: Math.min(a.bottom, b.bottom)
  });

  const rectOf = (rect) => ({
    x: round(rect.left + window.scrollX),
    y: round(rect.top + window.scrollY),
    width: round(Math.max(0, rect.right - rect.left)),
    height: round(Math.max(0, rect.bottom - rect.top))
  });

  const isClosedDetailsContent = (element) => {
    const details = element.closest('details:not([open])');
    if (!details) return false;
    const summary = details.querySelector(':scope > summary');
    return !summary || !summary.contains(element);
  };

  const clipsAxis = (value) => ['hidden', 'clip', 'auto', 'scroll'].includes(String(value || '').toLowerCase());

  const visibilityOf = (element) => {
    if (!(element instanceof Element)) return null;
    if (element.closest('[hidden], [inert], [aria-hidden="true"]')) return null;
    if (isClosedDetailsContent(element)) return null;

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
        const bounds = {
          left: clipsAxis(style.overflowX) ? clip.left : -Infinity,
          right: clipsAxis(style.overflowX) ? clip.right : Infinity,
          top: clipsAxis(style.overflowY) ? clip.top : -Infinity,
          bottom: clipsAxis(style.overflowY) ? clip.bottom : Infinity
        };
        visibleRect = intersect(visibleRect, bounds);
        if (area(visibleRect) <= 0) return null;
      }

      ancestor = ancestor.parentElement;
    }

    const sourceArea = Math.max(1, source.width * source.height);
    const visibleArea = area(visibleRect);
    if (visibleArea <= 0) return null;

    return {
      source,
      visibleRect,
      ratio: Math.max(0, Math.min(1, visibleArea / sourceArea)),
      clipped: visibleArea + 0.5 < sourceArea
    };
  };

  const labelledBy = (element) => {
    const value = element.getAttribute('aria-labelledby');
    if (!value) return '';
    return cleanText(value.split(/\s+/).map((id) => document.getElementById(id)?.textContent || '').join(' '));
  };

  const describedLabel = (element) => {
    const explicit = element.getAttribute('aria-label');
    if (explicit) return cleanText(explicit);
    return labelledBy(element);
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
    describedLabel(element)
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
    '[tabindex]:not([tabindex="-1"])',
    '[data-taskforge-agent-role]',
    '[data-taskforge-agent-action]',
    '[data-taskforge-automation-id]'
  ];

  const interactiveSelector = [
    'a[href]', 'button', 'input:not([type="hidden"])', 'select', 'textarea', 'summary',
    '[contenteditable="true"]', '[role="button"]', '[role="link"]', '[role="checkbox"]',
    '[role="radio"]', '[role="switch"]', '[role="tab"]', '[role="menuitem"]', '[role="option"]',
    '[tabindex]:not([tabindex="-1"])'
  ].join(',');

  const unique = [];
  const seen = new Set();
  document.querySelectorAll(selectors.join(',')).forEach((element) => {
    if (seen.has(element)) return;
    const visibility = visibilityOf(element);
    if (!visibility) return;
    seen.add(element);
    unique.push({ element, visibility, interactive: element.matches(interactiveSelector) });
  });

  const elements = unique.map(({ element, visibility, interactive }, index) => {
    const id = `tf${index + 1}`;
    element.setAttribute('data-taskforge-agent-id', id);
    const rect = visibility.source;
    const visibleRect = visibility.visibleRect;
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
      automationId: element.getAttribute('data-taskforge-automation-id') || null,
      automationRole: element.getAttribute('data-taskforge-agent-role') || null,
      automationAction: element.getAttribute('data-taskforge-agent-action') || null,
      automationState: element.getAttribute('data-taskforge-agent-state') || null,
      automationKind: element.getAttribute('data-taskforge-agent-kind') || null,
      value: element instanceof HTMLInputElement && String(element.type || '').toLowerCase() === 'password'
        ? null
        : (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement)
          ? String(element.value ?? '')
          : null,
      options: element instanceof HTMLSelectElement
        ? Array.from(element.options).map((option) => ({
            value: String(option.value ?? ''),
            label: cleanText(option.label || option.textContent, 300),
            selected: option.selected,
            disabled: option.disabled
          }))
        : [],
      interactive,
      disabled: Boolean(element.disabled || element.getAttribute('aria-disabled') === 'true'),
      checked: Boolean(element.checked || element.getAttribute('aria-checked') === 'true'),
      selected: Boolean(element.selected || element.getAttribute('aria-selected') === 'true'),
      inViewport: visibleRect.bottom > 0 && visibleRect.right > 0 && visibleRect.top < window.innerHeight && visibleRect.left < window.innerWidth,
      clipped: visibility.clipped,
      visibleRatio: round(visibility.ratio),
      bounds: rectOf({ left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom }),
      visibleBounds: rectOf(visibleRect),
      _semanticName: semanticName(element)
    };
  });

  const interactiveElements = elements.filter((x) => x.interactive);
  const smallTargets = interactiveElements.filter((x) => x.visibleBounds.width < 24 || x.visibleBounds.height < 24);
  const touchTargetsBelow44 = interactiveElements.filter((x) => x.visibleBounds.width < 44 || x.visibleBounds.height < 44);
  const unlabelled = interactiveElements.filter((x) => !x._semanticName);
  elements.forEach((element) => delete element._semanticName);

  const images = Array.from(document.images).filter((image) => visibilityOf(image));
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
      if (overflowElements.length >= 50) return;
      const visibility = visibilityOf(element);
      if (!visibility) return;
      const rect = visibility.visibleRect;
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
    .filter((heading) => visibilityOf(heading))
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
      interactiveElementCount: interactiveElements.length,
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
