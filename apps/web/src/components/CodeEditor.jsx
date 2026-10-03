import React, {
  useEffect,
  useMemo,
  useRef,
  useState,
  useCallback,
} from "react";
import { useEditorUiSettings, useUiTheme } from "../contexts/UiSettingsContext";
import { logFrontendEvent } from "../devtools/frontendDiagnostics";

function getMonacoVsPath() {
  const base = String(process.env.PUBLIC_URL || "").replace(/\/$/, "");
  return `${base}/monaco/vs`;
}

let monacoReactModulePromise = null;

function loadMonacoReactModule() {
  if (!monacoReactModulePromise) {
    monacoReactModulePromise = import("@monaco-editor/react")
      .then((module) => {
        if (!module?.default || !module?.loader) {
          throw new Error("Monaco React module is incomplete");
        }
        module.loader.config({ paths: { vs: getMonacoVsPath() } });
        return module;
      })
      .catch((error) => {
        monacoReactModulePromise = null;
        throw error;
      });
  }
  return monacoReactModulePromise;
}

function isUsableMonaco(monaco) {
  return Boolean(
    monaco &&
    monaco.editor &&
    typeof monaco.editor.create === "function" &&
    typeof monaco.editor.getModel === "function" &&
    typeof monaco.editor.createModel === "function" &&
    monaco.Uri &&
    typeof monaco.Uri.parse === "function",
  );
}

class MonacoCrashBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { crashed: false };
  }

  static getDerivedStateFromError() {
    return { crashed: true };
  }

  componentDidCatch(error) {
    logFrontendEvent('react', 'monaco-error-boundary', { error }, 'error');
    this.props.onCrash?.(error);
  }

  componentDidUpdate(prevProps) {
    if (prevProps.resetKey !== this.props.resetKey && this.state.crashed) {
      this.setState({ crashed: false });
    }
  }

  render() {
    if (this.state.crashed) return this.props.fallback;
    return this.props.children;
  }
}

export function isPlainTextEditorStyle(value) {
  return value === "mono";
}

function cssRgbToHex(value, fallback) {
  const text = String(value || "").trim();
  const parts = text
    .match(/\d+(?:\.\d+)?/g)
    ?.slice(0, 3)
    .map((n) => Math.max(0, Math.min(255, Number(n))));
  if (!parts || parts.length < 3 || parts.some((n) => Number.isNaN(n)))
    return fallback;
  return parts
    .map((n) => Math.round(n).toString(16).padStart(2, "0"))
    .join("")
    .toUpperCase();
}

function readThemeHex(name, fallback) {
  const styles = getComputedStyle(document.documentElement);
  return cssRgbToHex(styles.getPropertyValue(name), fallback);
}

function withAlpha(hex, alpha) {
  const clean = String(hex || "")
    .replace("#", "")
    .slice(0, 6)
    .padEnd(6, "0");
  const a = Math.round(Math.max(0, Math.min(1, alpha)) * 255)
    .toString(16)
    .padStart(2, "0");
  return `${clean}${a}`.toUpperCase();
}

function defineDynamicMonacoThemes(monaco) {
  if (!monaco?.editor?.defineTheme || typeof document === "undefined") return;

  const accent = readThemeHex("--accent", "2563EB");
  const accent2 = readThemeHex("--accent2", accent);
  const accent3 = readThemeHex("--accent3", accent2);
  const text = readThemeHex("--text", "0F172A");
  const muted = readThemeHex("--text-muted", "64748B");
  const card = readThemeHex("--card", "FFFFFF");
  const page = readThemeHex("--page-bg", "F8FAFC");
  const border = readThemeHex("--border", "CBD5E1");
  const mutedSurface = readThemeHex("--muted", page);

  const darkRules = [
    { token: "", foreground: text },
    { token: "comment", foreground: muted },
    { token: "string", foreground: accent3 },
    { token: "number", foreground: accent2 },
    { token: "keyword", foreground: accent, fontStyle: "bold" },
    { token: "type", foreground: accent2 },
    { token: "function", foreground: text },
    { token: "identifier", foreground: text },
  ];

  const lightRules = [
    { token: "comment", foreground: muted },
    { token: "string", foreground: accent2 },
    { token: "number", foreground: accent },
    { token: "keyword", foreground: accent, fontStyle: "bold" },
    { token: "type", foreground: accent2 },
    { token: "function", foreground: text },
    { token: "identifier", foreground: text },
  ];

  monaco.editor.defineTheme("taskforge-dynamic-dark-color", {
    base: "vs-dark",
    inherit: true,
    rules: darkRules,
    colors: {
      "editor.background": `#${card}`,
      "editorGutter.background": `#${card}`,
      "editor.foreground": `#${text}`,
      "editorLineNumber.foreground": `#${withAlpha(muted, 0.72)}`,
      "editorLineNumber.activeForeground": `#${accent2}`,
      "editor.selectionBackground": `#${withAlpha(accent, 0.3)}`,
      "editor.inactiveSelectionBackground": `#${withAlpha(accent, 0.18)}`,
      "editor.lineHighlightBackground": `#${withAlpha(accent, 0.1)}`,
      "editorCursor.foreground": `#${accent2}`,
      "scrollbarSlider.background": `#${withAlpha(border, 0.4)}`,
      "scrollbarSlider.hoverBackground": `#${withAlpha(accent, 0.38)}`,
      "scrollbarSlider.activeBackground": `#${withAlpha(accent, 0.55)}`,
      "editorIndentGuide.background": `#${withAlpha(border, 0.38)}`,
      "editorIndentGuide.activeBackground": `#${withAlpha(accent, 0.55)}`,
      "editorWidget.background": `#${mutedSurface}`,
      "editorWidget.border": `#${withAlpha(border, 0.65)}`,
      "editorSuggestWidget.background": `#${mutedSurface}`,
      "editorSuggestWidget.border": `#${withAlpha(border, 0.65)}`,
      "editorSuggestWidget.selectedBackground": `#${withAlpha(accent, 0.25)}`,
      "list.hoverBackground": `#${withAlpha(accent, 0.14)}`,
      focusBorder: `#${accent}`,
    },
  });

  monaco.editor.defineTheme("taskforge-dynamic-light-color", {
    base: "vs",
    inherit: true,
    rules: lightRules,
    colors: {
      "editor.background": `#${card}`,
      "editorGutter.background": `#${card}`,
      "editor.foreground": `#${text}`,
      "editorLineNumber.foreground": `#${muted}`,
      "editorLineNumber.activeForeground": `#${accent}`,
      "editor.selectionBackground": `#${withAlpha(accent, 0.24)}`,
      "editor.inactiveSelectionBackground": `#${withAlpha(accent, 0.14)}`,
      "editor.lineHighlightBackground": `#${withAlpha(page, 0.92)}`,
      "editorIndentGuide.background": `#${withAlpha(border, 0.6)}`,
      "editorIndentGuide.activeBackground": `#${withAlpha(accent, 0.45)}`,
      focusBorder: `#${accent}`,
    },
  });
}

function PlainTextCodeEditor({
  value,
  onChange,
  height,
  readOnly,
  isDark,
  automationId,
  automationRole,
  automationAction,
  automationState,
  onRetry = null,
}) {
  return (
    <div
      className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
      style={{ width: "100%", position: "relative" }}
      data-taskforge-editor-kind="plain-text"
    >
      {onRetry ? (
        <button
          type="button"
          onClick={onRetry}
          className="absolute right-2 top-2 z-10 rounded-lg border border-neutral-200 bg-white/80 px-2 py-1 text-xs text-neutral-700 shadow-sm backdrop-blur hover:bg-white dark:border-neutral-700 dark:bg-neutral-950/80 dark:text-neutral-200 dark:hover:bg-neutral-900"
        >
          Повторить Monaco
        </button>
      ) : null}
      <textarea
        className="code-editor-plain-textarea w-full resize-none outline-none"
        value={value ?? ""}
        onChange={(event) => onChange?.(event.target.value)}
        readOnly={readOnly}
        spellCheck={false}
        autoCorrect="off"
        autoCapitalize="none"
        autoComplete="off"
        data-taskforge-automation-id={automationId || undefined}
        data-taskforge-agent-role={automationRole || undefined}
        data-taskforge-agent-action={automationAction || undefined}
        data-taskforge-agent-state={automationState || undefined}
        aria-label={automationRole === "code-editor" ? "Код решения" : "Текстовый редактор кода"}
        style={{
          height,
          padding: 8,
          fontSize: 14,
          lineHeight: "20px",
          letterSpacing: 0.2,
          color: isDark ? "#D8DEE9" : "rgb(var(--text))",
          background: isDark ? "#0f1115" : "rgb(var(--card))",
          fontFamily:
            'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace',
        }}
      />
    </div>
  );
}

function MonacoCodeEditor({
  language = "cpp",
  value,
  onChange,
  modelPath,
  height = 320,
  lineNumbers = "on",
  readOnly = false,
  automationId = null,
  automationRole = null,
  automationAction = null,
  automationState = null,
  isDark,
}) {
  const [loadFailed, setLoadFailed] = useState(false);
  const [monacoReady, setMonacoReady] = useState(false);
  const [MonacoEditorComponent, setMonacoEditorComponent] = useState(null);
  const [retryNonce, setRetryNonce] = useState(0);

  const wrapperRef = useRef(null);
  const editorRef = useRef(null);
  const monacoRef = useRef(null);
  const roRef = useRef(null);
  const cleanupRef = useRef(null);
  const applyThemeRef = useRef(null);

  const monacoLang = useMemo(() => {
    switch (String(language || "").toLowerCase()) {
      case "c++":
      case "cpp":
        return "cpp";
      case "c#":
      case "cs":
      case "csharp":
        return "csharp";
      case "python":
        return "python";
      case "py":
      case "js":
      case "node":
      case "nodejs":
      case "javascript":
        return "javascript";
      case "pas":
      case "pascal":
        return "pascal";
      case "java":
        return "java";
      case "sql":
        return "sql";
      default:
        return "plaintext";
    }
  }, [language]);

  const pickThemeName = useCallback(() => (
    isDark ? "taskforge-dynamic-dark-color" : "taskforge-dynamic-light-color"
  ), [isDark]);

  const applyTheme = useCallback((monaco) => {
    if (!monaco?.editor) return;
    try {
      defineDynamicMonacoThemes(monaco);
      monaco.editor.setTheme(pickThemeName());
    } catch {}
  }, [pickThemeName]);

  const handleValueChange = useCallback(
    (nextValue) => onChange?.(nextValue ?? ""),
    [onChange],
  );

  const handleEditorCrash = useCallback(() => {
    setLoadFailed(true);
  }, []);

  const editorOptions = useMemo(
    () => ({
      readOnly,
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
      wordWrap: "on",
      tabSize: 2,
      insertSpaces: true,
      renderWhitespace: "selection",
      renderLineHighlight: "line",
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      mouseWheelZoom: true,
    }),
    [lineNumbers, readOnly],
  );

  const handleBeforeMount = useCallback((monaco) => {
    applyTheme(monaco);
  }, [applyTheme]);

  const relayout = useCallback(() => {
    const ed = editorRef.current;
    const el = wrapperRef.current;
    if (!ed || !el) return;
    const w = Math.max(0, el.clientWidth);
    const h = typeof height === "number" ? height : el.clientHeight || 0;
    requestAnimationFrame(() => {
      try {
        ed.layout({ width: w, height: h });
      } catch {}
    });
  }, [height]);

  const handleMount = useCallback((editor, monaco) => {
    cleanupRef.current?.();
    editorRef.current = editor;
    monacoRef.current = monaco;
    setLoadFailed(false);

    applyTheme(monaco);

    if (
      wrapperRef.current &&
      !roRef.current &&
      typeof ResizeObserver !== "undefined"
    ) {
      roRef.current = new ResizeObserver(() => relayout());
      roRef.current.observe(wrapperRef.current);
    }

    const onWinResize = () => relayout();
    window.addEventListener("resize", onWinResize);
    window.addEventListener("orientationchange", onWinResize);

    cleanupRef.current = () => {
      window.removeEventListener("resize", onWinResize);
      window.removeEventListener("orientationchange", onWinResize);
      roRef.current?.disconnect();
      roRef.current = null;
    };

    relayout();
  }, [applyTheme, relayout]);

  useEffect(() => {
    applyThemeRef.current = applyTheme;
    if (monacoRef.current?.editor) applyTheme(monacoRef.current);
  }, [applyTheme]);

  useEffect(() => () => {
    cleanupRef.current?.();
    cleanupRef.current = null;
  }, []);

  useEffect(() => {
    let alive = true;
    let cancelable = null;

    cleanupRef.current?.();
    cleanupRef.current = null;
    editorRef.current = null;
    monacoRef.current = null;
    setLoadFailed(false);
    setMonacoReady(false);
    setMonacoEditorComponent(null);

    const timer = window.setTimeout(() => {
      if (alive && !editorRef.current) setLoadFailed(true);
    }, 7000);

    loadMonacoReactModule()
      .then((module) => {
        if (!alive) return null;
        setMonacoEditorComponent(() => module.default);
        cancelable = module.loader.init();
        return cancelable;
      })
      .then((monaco) => {
        if (!alive || !monaco) return;
        if (!isUsableMonaco(monaco)) {
          setLoadFailed(true);
          return;
        }
        monacoRef.current = monaco;
        applyThemeRef.current?.(monaco);
        setMonacoReady(true);
        setLoadFailed(false);
      })
      .catch((error) => {
        logFrontendEvent('react', 'monaco-load-failed', { error }, 'warn');
        if (alive) setLoadFailed(true);
      });

    return () => {
      alive = false;
      window.clearTimeout(timer);
      if (!editorRef.current && typeof cancelable?.cancel === "function") {
        cancelable.cancel();
      }
    };
  }, [retryNonce]);

  const fallbackEditor = (
    <PlainTextCodeEditor
      value={value}
      onChange={handleValueChange}
      height={height}
      readOnly={readOnly}
      isDark={isDark}
      automationId={automationId}
      automationRole={automationRole}
      automationAction={automationAction}
      automationState={automationState}
      onRetry={() => setRetryNonce((current) => current + 1)}
    />
  );

  if (loadFailed) return fallbackEditor;

  if (!monacoReady || !MonacoEditorComponent) {
    return (
      <div
        className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
        style={{ width: "100%" }}
      >
        <div
          className="flex items-center justify-center text-sm text-neutral-500"
          style={{
            height,
            background: isDark ? "#0f1115" : "rgb(var(--card))",
          }}
        >
          Загрузка редактора...
        </div>
      </div>
    );
  }

  return (
    <div
      ref={wrapperRef}
      className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
      style={{ width: "100%" }}
      data-taskforge-editor-kind="monaco"
    >
      <MonacoCrashBoundary
        resetKey={`${retryNonce}-${monacoLang}-${isDark ? "dark" : "light"}`}
        fallback={fallbackEditor}
        onCrash={handleEditorCrash}
      >
        <MonacoEditorComponent
          height={height}
          language={monacoLang}
          path={modelPath}
          theme={pickThemeName()}
          value={value}
          onChange={handleValueChange}
          beforeMount={handleBeforeMount}
          onMount={handleMount}
          loading={
            <div
              className="flex items-center justify-center text-sm text-neutral-500"
              style={{
                height,
                background: isDark ? "#0f1115" : "rgb(var(--card))",
              }}
            >
              Загрузка редактора...
            </div>
          }
          options={editorOptions}
        />
      </MonacoCrashBoundary>
    </div>
  );
}

function CodeEditor(props) {
  const { mode } = useUiTheme();
  const { codeEditorStyle } = useEditorUiSettings();
  const isDark = mode === "dark";
  const browserAutomation = typeof window !== "undefined"
    && window.__TASKFORGE_BROWSER_AUTOMATION__ === true;
  const usePlainText = browserAutomation || isPlainTextEditorStyle(codeEditorStyle);

  if (usePlainText) {
    return (
      <PlainTextCodeEditor
        value={props.value}
        onChange={props.onChange}
        height={props.height ?? 320}
        readOnly={props.readOnly ?? false}
        isDark={isDark}
        automationId={props.automationId}
        automationRole={props.automationRole}
        automationAction={props.automationAction}
        automationState={props.automationState}
      />
    );
  }

  return <MonacoCodeEditor {...props} isDark={isDark} />;
}

export default React.memo(CodeEditor);
