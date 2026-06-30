import React, { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import Editor, { loader } from '@monaco-editor/react';

const MONACO_BASE_PATH = `${String(process.env.PUBLIC_URL || '').replace(/\/$/, '')}/monaco/vs`;
loader.config({ paths: { vs: MONACO_BASE_PATH || '/monaco/vs' } });

const CODE_EDITOR_STYLE_KEY = 'codeEditorStyle';
const MONO_TOKENS = [
  '',
  'comment',
  'string',
  'number',
  'keyword',
  'type',
  'function',
  'identifier',
  'operator',
  'delimiter',
  'namespace',
  'class',
  'variable',
  'constant',
  'regexp',
];

function normalizeEditorStyle(value) {
  return value === 'mono' ? 'mono' : 'color';
}

function readCodeEditorStyle() {
  try {
    return normalizeEditorStyle(localStorage.getItem(CODE_EDITOR_STYLE_KEY));
  } catch {
    return 'color';
  }
}

function cssRgbToHex(value, fallback) {
  const text = String(value || '').trim();
  const parts = text.match(/\d+(?:\.\d+)?/g)?.slice(0, 3).map((n) => Math.max(0, Math.min(255, Number(n))));
  if (!parts || parts.length < 3 || parts.some((n) => Number.isNaN(n))) return fallback;
  return parts.map((n) => Math.round(n).toString(16).padStart(2, '0')).join('').toUpperCase();
}

function readThemeHex(name, fallback) {
  const styles = getComputedStyle(document.documentElement);
  return cssRgbToHex(styles.getPropertyValue(name), fallback);
}

function withAlpha(hex, alpha) {
  const clean = String(hex || '').replace('#', '').slice(0, 6).padEnd(6, '0');
  const a = Math.round(Math.max(0, Math.min(1, alpha)) * 255).toString(16).padStart(2, '0');
  return `${clean}${a}`.toUpperCase();
}

function defineDynamicMonacoThemes(monaco) {
  if (!monaco || typeof document === 'undefined') return;

  const accent = readThemeHex('--accent', '2563EB');
  const accent2 = readThemeHex('--accent2', accent);
  const accent3 = readThemeHex('--accent3', accent2);
  const text = readThemeHex('--text', '0F172A');
  const muted = readThemeHex('--text-muted', '64748B');
  const card = readThemeHex('--card', 'FFFFFF');
  const page = readThemeHex('--page-bg', 'F8FAFC');
  const border = readThemeHex('--border', 'CBD5E1');
  const widgetBg = readThemeHex('--surface-2', page);

  monaco.editor.defineTheme('taskforge-dynamic-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: '', foreground: text },
      { token: 'comment', foreground: muted },
      { token: 'string', foreground: accent3 },
      { token: 'number', foreground: accent2 },
      { token: 'keyword', foreground: accent, fontStyle: 'bold' },
      { token: 'type', foreground: accent2 },
      { token: 'function', foreground: text },
      { token: 'identifier', foreground: text },
    ],
    colors: {
      'editor.background': `#${card}`,
      'editorGutter.background': `#${card}`,
      'editor.foreground': `#${text}`,
      'editorLineNumber.foreground': `#${withAlpha(muted, 0.72)}`,
      'editorLineNumber.activeForeground': `#${accent2}`,
      'editor.selectionBackground': `#${withAlpha(accent, 0.30)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(accent, 0.18)}`,
      'editor.lineHighlightBackground': `#${withAlpha(accent, 0.10)}`,
      'editorCursor.foreground': `#${accent2}`,
      'scrollbarSlider.background': `#${withAlpha(border, 0.40)}`,
      'scrollbarSlider.hoverBackground': `#${withAlpha(accent, 0.38)}`,
      'scrollbarSlider.activeBackground': `#${withAlpha(accent, 0.55)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.38)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(accent, 0.55)}`,
      'editorWidget.background': `#${widgetBg}`,
      'editorWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.background': `#${widgetBg}`,
      'editorSuggestWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.selectedBackground': `#${withAlpha(accent, 0.25)}`,
      'list.hoverBackground': `#${withAlpha(accent, 0.14)}`,
      'focusBorder': `#${accent}`,
    },
  });

  monaco.editor.defineTheme('taskforge-dynamic-light', {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'comment', foreground: muted },
      { token: 'string', foreground: accent2 },
      { token: 'number', foreground: accent },
      { token: 'keyword', foreground: accent, fontStyle: 'bold' },
      { token: 'type', foreground: accent2 },
      { token: 'function', foreground: text },
      { token: 'identifier', foreground: text },
    ],
    colors: {
      'editor.background': `#${card}`,
      'editorGutter.background': `#${card}`,
      'editor.foreground': `#${text}`,
      'editorLineNumber.foreground': `#${muted}`,
      'editorLineNumber.activeForeground': `#${accent}`,
      'editor.selectionBackground': `#${withAlpha(accent, 0.24)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(accent, 0.14)}`,
      'editor.lineHighlightBackground': `#${withAlpha(page, 0.92)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.60)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(accent, 0.45)}`,
      'editorWidget.background': `#${widgetBg}`,
      'editorWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.background': `#${widgetBg}`,
      'editorSuggestWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.selectedBackground': `#${withAlpha(accent, 0.18)}`,
      'focusBorder': `#${accent}`,
    },
  });

  monaco.editor.defineTheme('taskforge-mono-dark', {
    base: 'vs-dark',
    inherit: false,
    rules: MONO_TOKENS.map((token) => ({
      token,
      foreground: token === 'comment' ? muted : text,
      fontStyle: token === 'keyword' || token === 'type' ? 'bold' : '',
    })),
    colors: {
      'editor.background': `#${card}`,
      'editorGutter.background': `#${card}`,
      'editor.foreground': `#${text}`,
      'editorLineNumber.foreground': `#${withAlpha(muted, 0.72)}`,
      'editorLineNumber.activeForeground': `#${text}`,
      'editor.selectionBackground': `#${withAlpha(text, 0.22)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(text, 0.12)}`,
      'editor.lineHighlightBackground': `#${withAlpha(text, 0.07)}`,
      'editorCursor.foreground': `#${text}`,
      'scrollbarSlider.background': `#${withAlpha(border, 0.40)}`,
      'scrollbarSlider.hoverBackground': `#${withAlpha(text, 0.22)}`,
      'scrollbarSlider.activeBackground': `#${withAlpha(text, 0.34)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.38)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(text, 0.35)}`,
      'editorWidget.background': `#${widgetBg}`,
      'editorWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.background': `#${widgetBg}`,
      'editorSuggestWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.selectedBackground': `#${withAlpha(text, 0.15)}`,
      'focusBorder': `#${text}`,
    },
  });

  monaco.editor.defineTheme('taskforge-mono-light', {
    base: 'vs',
    inherit: false,
    rules: MONO_TOKENS.map((token) => ({
      token,
      foreground: token === 'comment' ? muted : text,
      fontStyle: token === 'keyword' || token === 'type' ? 'bold' : '',
    })),
    colors: {
      'editor.background': `#${card}`,
      'editorGutter.background': `#${card}`,
      'editor.foreground': `#${text}`,
      'editorLineNumber.foreground': `#${muted}`,
      'editorLineNumber.activeForeground': `#${text}`,
      'editor.selectionBackground': `#${withAlpha(text, 0.16)}`,
      'editor.inactiveSelectionBackground': `#${withAlpha(text, 0.08)}`,
      'editor.lineHighlightBackground': `#${withAlpha(page, 0.92)}`,
      'editorIndentGuide.background': `#${withAlpha(border, 0.60)}`,
      'editorIndentGuide.activeBackground': `#${withAlpha(text, 0.28)}`,
      'editorWidget.background': `#${widgetBg}`,
      'editorWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.background': `#${widgetBg}`,
      'editorSuggestWidget.border': `#${withAlpha(border, 0.65)}`,
      'editorSuggestWidget.selectedBackground': `#${withAlpha(text, 0.10)}`,
      'focusBorder': `#${text}`,
    },
  });
}

export default function CodeEditor({
  language = 'cpp',
  value,
  onChange,
  height = 320,
  lineNumbers = 'on',
}) {
  const [isDark, setIsDark] = useState(
    document.documentElement.classList.contains('dark')
  );
  const [editorStyle, setEditorStyle] = useState(readCodeEditorStyle);
  const [isMounted, setIsMounted] = useState(false);
  const [fallbackMode, setFallbackMode] = useState(false);
  const [retryNonce, setRetryNonce] = useState(0);

  const wrapperRef = useRef(null);
  const editorRef = useRef(null);
  const monacoRef = useRef(null);
  const roRef = useRef(null);
  const cleanupRef = useRef(null);

  const monacoLang = useMemo(() => {
    switch (String(language || '').toLowerCase()) {
      case 'c++':
      case 'cpp':
        return 'cpp';
      case 'c#':
      case 'cs':
      case 'csharp':
        return 'csharp';
      case 'python':
      case 'py':
        return 'python';
      case 'js':
      case 'node':
      case 'nodejs':
      case 'javascript':
        return 'javascript';
      case 'pas':
      case 'pascal':
        return 'pascal';
      case 'java':
        return 'java';
      default:
        return 'plaintext';
    }
  }, [language]);

  const pickThemeName = useCallback(() => {
    const dark = document.documentElement.classList.contains('dark');
    if (editorStyle === 'mono') return dark ? 'taskforge-mono-dark' : 'taskforge-mono-light';
    return dark ? 'taskforge-dynamic-dark' : 'taskforge-dynamic-light';
  }, [editorStyle]);

  const handleBeforeMount = useCallback((monaco) => {
    defineDynamicMonacoThemes(monaco);
  }, []);

  const relayout = useCallback(() => {
    const ed = editorRef.current;
    const el = wrapperRef.current;
    if (!ed || !el) return;
    const w = Math.max(0, el.clientWidth);
    const h = typeof height === 'number' ? height : el.clientHeight || 0;
    requestAnimationFrame(() => ed.layout({ width: w, height: h }));
  }, [height]);

  const handleMount = useCallback((editor, monaco) => {
    cleanupRef.current?.();
    editorRef.current = editor;
    monacoRef.current = monaco;
    setIsMounted(true);
    setFallbackMode(false);

    defineDynamicMonacoThemes(monaco);
    monaco.editor.setTheme(pickThemeName());

    if (wrapperRef.current) {
      roRef.current = new ResizeObserver(() => relayout());
      roRef.current.observe(wrapperRef.current);
    }

    const onWinResize = () => relayout();
    window.addEventListener('resize', onWinResize);
    window.addEventListener('orientationchange', onWinResize);

    cleanupRef.current = () => {
      window.removeEventListener('resize', onWinResize);
      window.removeEventListener('orientationchange', onWinResize);
      roRef.current?.disconnect();
      roRef.current = null;
    };

    setTimeout(relayout, 0);
    setTimeout(relayout, 180);
  }, [pickThemeName, relayout]);

  useEffect(() => {
    return () => {
      cleanupRef.current?.();
      cleanupRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (fallbackMode) return undefined;
    setIsMounted(false);
    editorRef.current = null;
    const timer = window.setTimeout(() => {
      if (!editorRef.current) setFallbackMode(true);
    }, 8500);
    return () => window.clearTimeout(timer);
  }, [fallbackMode, monacoLang, retryNonce]);

  useEffect(() => {
    const refreshTheme = () => {
      const dark = document.documentElement.classList.contains('dark');
      const nextStyle = readCodeEditorStyle();
      setIsDark(dark);
      setEditorStyle(nextStyle);
      try {
        if (monacoRef.current) defineDynamicMonacoThemes(monacoRef.current);
        monacoRef.current?.editor?.setTheme(dark
          ? (nextStyle === 'mono' ? 'taskforge-mono-dark' : 'taskforge-dynamic-dark')
          : (nextStyle === 'mono' ? 'taskforge-mono-light' : 'taskforge-dynamic-light'));
      } catch {}
    };

    const mo = new MutationObserver(refreshTheme);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    window.addEventListener('tf-ui-settings-changed', refreshTheme);
    return () => {
      mo.disconnect();
      window.removeEventListener('tf-ui-settings-changed', refreshTheme);
    };
  }, []);

  useEffect(() => {
    try {
      if (monacoRef.current) defineDynamicMonacoThemes(monacoRef.current);
      monacoRef.current?.editor?.setTheme(pickThemeName());
    } catch {}
  }, [isDark, editorStyle, pickThemeName]);

  if (fallbackMode) {
    return (
      <div
        ref={wrapperRef}
        className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
        style={{ width: '100%' }}
      >
        <div className="flex items-center justify-between gap-3 border-b border-[rgba(var(--border)/0.65)] bg-[rgba(var(--card)/0.72)] px-3 py-2 text-xs text-neutral-500 dark:text-neutral-400">
          <span>Редактор не загрузился, открыт простой режим.</span>
          <button
            type="button"
            className="rounded-lg border border-[rgba(var(--border)/0.75)] px-2 py-1 text-[rgb(var(--text))] hover:bg-[rgba(var(--border)/0.18)]"
            onClick={() => {
              setFallbackMode(false);
              setRetryNonce((x) => x + 1);
            }}
          >
            Повторить
          </button>
        </div>
        <textarea
          className="w-full resize-y border-0 bg-[rgb(var(--card))] px-3 py-2 font-mono text-sm leading-5 outline-none"
          style={{ minHeight: typeof height === 'number' ? height : 320, height }}
          value={value || ''}
          onChange={(event) => onChange?.(event.target.value)}
          spellCheck={false}
        />
      </div>
    );
  }

  return (
    <div
      ref={wrapperRef}
      className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
      style={{ width: '100%' }}
    >
      <Editor
        key={`${monacoLang}-${editorStyle}-${retryNonce}`}
        height={height}
        language={monacoLang}
        theme={pickThemeName()}
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        loading={(
          <div
            className="flex items-center justify-center bg-[rgb(var(--card))] text-sm text-neutral-500 dark:text-neutral-400"
            style={{ height: typeof height === 'number' ? height : 320 }}
          >
            Загрузка редактора…
          </div>
        )}
        options={{
          lineNumbers,
          lineNumbersMinChars: 2,
          lineDecorationsWidth: 12,
          glyphMargin: false,
          folding: false,
          fontSize: 14,
          lineHeight: 20,
          letterSpacing: 0.2,
          padding: { top: 8, bottom: 8 },
          minimap: { enabled: false },
          automaticLayout: false,
          wordWrap: 'on',
          tabSize: 2,
          insertSpaces: true,
          renderWhitespace: 'selection',
          renderLineHighlight: 'line',
          scrollBeyondLastLine: false,
          smoothScrolling: true,
          mouseWheelZoom: true,
          'semanticHighlighting.enabled': editorStyle !== 'mono',
        }}
      />
      {!isMounted ? null : null}
    </div>
  );
}
