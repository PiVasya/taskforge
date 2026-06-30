import React, {
  useEffect,
  useMemo,
  useRef,
  useState,
  useCallback,
} from "react";
import Editor, { loader } from "@monaco-editor/react";

function getMonacoVsPath() {
  const base = String(process.env.PUBLIC_URL || "").replace(/\/$/, "");
  return `${base}/monaco/vs`;
}

loader.config({ paths: { vs: getMonacoVsPath() } });

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

const CODE_EDITOR_STYLE_KEY = "codeEditorStyle";

function normalizeEditorStyle(value) {
  return value === "mono" ? "mono" : "color";
}

function readCodeEditorStyle() {
  try {
    return normalizeEditorStyle(localStorage.getItem(CODE_EDITOR_STYLE_KEY));
  } catch {
    return "color";
  }
}

function isDarkMode() {
  return (
    typeof document !== "undefined" &&
    document.documentElement.classList.contains("dark")
  );
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

function defineDynamicMonacoThemes(monaco, editorStyle = "color") {
  if (!monaco?.editor?.defineTheme || typeof document === "undefined") return;

  const style = normalizeEditorStyle(editorStyle);
  const accent = readThemeHex("--accent", "2563EB");
  const accent2 = readThemeHex("--accent2", accent);
  const accent3 = readThemeHex("--accent3", accent2);
  const text = readThemeHex("--text", "0F172A");
  const muted = readThemeHex("--text-muted", "64748B");
  const card = readThemeHex("--card", "FFFFFF");
  const page = readThemeHex("--page-bg", "F8FAFC");
  const border = readThemeHex("--border", "CBD5E1");

  const darkRules =
    style === "mono"
      ? [
          { token: "", foreground: "D8DEE9" },
          { token: "comment", foreground: "7B8794" },
          { token: "string", foreground: "D8DEE9" },
          { token: "number", foreground: "D8DEE9" },
          { token: "keyword", foreground: "D8DEE9", fontStyle: "bold" },
          { token: "type", foreground: "D8DEE9" },
          { token: "function", foreground: "F8FAFC" },
          { token: "identifier", foreground: "D8DEE9" },
        ]
      : [
          { token: "", foreground: "D8DEE9" },
          { token: "comment", foreground: muted },
          { token: "string", foreground: accent3 },
          { token: "number", foreground: accent2 },
          { token: "keyword", foreground: accent, fontStyle: "bold" },
          { token: "type", foreground: accent2 },
          { token: "function", foreground: "F8FAFC" },
          { token: "identifier", foreground: "D8DEE9" },
        ];

  const lightRules =
    style === "mono"
      ? [
          { token: "", foreground: text },
          { token: "comment", foreground: muted },
          { token: "string", foreground: text },
          { token: "number", foreground: text },
          { token: "keyword", foreground: text, fontStyle: "bold" },
          { token: "type", foreground: text },
          { token: "function", foreground: text },
          { token: "identifier", foreground: text },
        ]
      : [
          { token: "comment", foreground: muted },
          { token: "string", foreground: accent2 },
          { token: "number", foreground: accent },
          { token: "keyword", foreground: accent, fontStyle: "bold" },
          { token: "type", foreground: accent2 },
          { token: "function", foreground: text },
          { token: "identifier", foreground: text },
        ];

  monaco.editor.defineTheme(`taskforge-dynamic-dark-${style}`, {
    base: "vs-dark",
    inherit: true,
    rules: darkRules,
    colors: {
      "editor.background": "#0f1115",
      "editorGutter.background": "#0f1115",
      "editor.foreground": "#D8DEE9",
      "editorLineNumber.foreground": `#${style === "mono" ? "5d6b7e" : withAlpha(muted, 0.72)}`,
      "editorLineNumber.activeForeground": `#${style === "mono" ? "a7b4c6" : accent2}`,
      "editor.selectionBackground": `#${style === "mono" ? "2a3344" : withAlpha(accent, 0.3)}`,
      "editor.inactiveSelectionBackground": `#${style === "mono" ? "202838" : withAlpha(accent, 0.18)}`,
      "editor.lineHighlightBackground": `#${style === "mono" ? "141821" : withAlpha(accent, 0.1)}`,
      "editorCursor.foreground": `#${style === "mono" ? "E5E7EB" : accent2}`,
      "scrollbarSlider.background": `#${withAlpha(border, 0.4)}`,
      "scrollbarSlider.hoverBackground": `#${style === "mono" ? "2a3a5299" : withAlpha(accent, 0.38)}`,
      "scrollbarSlider.activeBackground": `#${style === "mono" ? "2a3a52cc" : withAlpha(accent, 0.55)}`,
      "editorIndentGuide.background": `#${withAlpha(border, 0.38)}`,
      "editorIndentGuide.activeBackground": `#${style === "mono" ? "3a4150" : withAlpha(accent, 0.55)}`,
      "editorWidget.background": "#12151b",
      "editorWidget.border": `#${withAlpha(border, 0.65)}`,
      "editorSuggestWidget.background": "#12151b",
      "editorSuggestWidget.border": `#${withAlpha(border, 0.65)}`,
      "editorSuggestWidget.selectedBackground": `#${style === "mono" ? "1a2230" : withAlpha(accent, 0.25)}`,
      "list.hoverBackground": `#${style === "mono" ? "1a1f28" : withAlpha(accent, 0.14)}`,
      focusBorder: `#${style === "mono" ? "a7b4c6" : accent}`,
    },
  });

  monaco.editor.defineTheme(`taskforge-dynamic-light-${style}`, {
    base: "vs",
    inherit: true,
    rules: lightRules,
    colors: {
      "editor.background": `#${card}`,
      "editorGutter.background": `#${card}`,
      "editor.foreground": `#${text}`,
      "editorLineNumber.foreground": `#${muted}`,
      "editorLineNumber.activeForeground": `#${style === "mono" ? text : accent}`,
      "editor.selectionBackground": `#${style === "mono" ? withAlpha(border, 0.75) : withAlpha(accent, 0.24)}`,
      "editor.inactiveSelectionBackground": `#${style === "mono" ? withAlpha(border, 0.45) : withAlpha(accent, 0.14)}`,
      "editor.lineHighlightBackground": `#${withAlpha(page, 0.92)}`,
      "editorIndentGuide.background": `#${withAlpha(border, 0.6)}`,
      "editorIndentGuide.activeBackground": `#${style === "mono" ? withAlpha(text, 0.32) : withAlpha(accent, 0.45)}`,
      focusBorder: `#${style === "mono" ? text : accent}`,
    },
  });
}

export default function CodeEditor({
  language = "cpp",
  value,
  onChange,
  height = 320,
  lineNumbers = "on",
  readOnly = false,
}) {
  const [isDark, setIsDark] = useState(isDarkMode);
  const [editorStyle, setEditorStyle] = useState(readCodeEditorStyle);
  const [loadFailed, setLoadFailed] = useState(false);
  const [monacoReady, setMonacoReady] = useState(false);
  const [retryNonce, setRetryNonce] = useState(0);

  const wrapperRef = useRef(null);
  const editorRef = useRef(null);
  const monacoRef = useRef(null);
  const roRef = useRef(null);
  const cleanupRef = useRef(null);

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
      default:
        return "plaintext";
    }
  }, [language]);

  const pickThemeName = useCallback(() => {
    const dark = isDarkMode();
    return dark
      ? `taskforge-dynamic-dark-${editorStyle}`
      : `taskforge-dynamic-light-${editorStyle}`;
  }, [editorStyle]);

  const applyTheme = useCallback(
    (monaco) => {
      if (!monaco?.editor) return;
      try {
        defineDynamicMonacoThemes(monaco, editorStyle);
        monaco.editor.setTheme(pickThemeName());
      } catch {}
    },
    [editorStyle, pickThemeName],
  );

  const handleBeforeMount = useCallback(
    (monaco) => {
      applyTheme(monaco);
    },
    [applyTheme],
  );

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

  const handleMount = useCallback(
    (editor, monaco) => {
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
    },
    [applyTheme, relayout],
  );

  useEffect(() => {
    const refresh = () => {
      setIsDark(isDarkMode());
      setEditorStyle(readCodeEditorStyle());
    };

    const mo =
      typeof MutationObserver !== "undefined"
        ? new MutationObserver(refresh)
        : null;
    mo?.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class"],
    });
    window.addEventListener("tf-ui-settings-changed", refresh);
    window.addEventListener("storage", refresh);

    return () => {
      mo?.disconnect();
      window.removeEventListener("tf-ui-settings-changed", refresh);
      window.removeEventListener("storage", refresh);
      cleanupRef.current?.();
      cleanupRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!monacoRef.current?.editor) return;
    applyTheme(monacoRef.current);
  }, [applyTheme, isDark, editorStyle]);

  useEffect(() => {
    let alive = true;
    let cancelable = null;

    cleanupRef.current?.();
    cleanupRef.current = null;
    editorRef.current = null;
    monacoRef.current = null;
    setLoadFailed(false);
    setMonacoReady(false);

    const timer = window.setTimeout(() => {
      if (alive && !editorRef.current) setLoadFailed(true);
    }, 7000);

    try {
      cancelable = loader.init();
      cancelable
        .then((monaco) => {
          if (!alive) return;
          if (!isUsableMonaco(monaco)) {
            setLoadFailed(true);
            return;
          }
          monacoRef.current = monaco;
          applyTheme(monaco);
          setMonacoReady(true);
          setLoadFailed(false);
        })
        .catch(() => {
          if (alive) setLoadFailed(true);
        });
    } catch {
      setLoadFailed(true);
    }

    return () => {
      alive = false;
      window.clearTimeout(timer);
      if (!editorRef.current && typeof cancelable?.cancel === "function") {
        cancelable.cancel();
      }
    };
  }, [applyTheme, retryNonce]);

  const fallbackEditor = (
    <div
      className="code-editor-shell rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-800 min-w-0"
      style={{ width: "100%", position: "relative" }}
    >
      {loadFailed ? (
        <button
          type="button"
          onClick={() => setRetryNonce((v) => v + 1)}
          className="absolute right-2 top-2 z-10 rounded-lg border border-neutral-200 bg-white/80 px-2 py-1 text-xs text-neutral-700 shadow-sm backdrop-blur hover:bg-white dark:border-neutral-700 dark:bg-neutral-950/80 dark:text-neutral-200 dark:hover:bg-neutral-900"
        >
          Повторить
        </button>
      ) : null}
      <textarea
        className="w-full resize-none outline-none"
        value={value ?? ""}
        onChange={(e) => onChange?.(e.target.value)}
        readOnly={readOnly}
        spellCheck={false}
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

  if (loadFailed) {
    return fallbackEditor;
  }

  if (!monacoReady) {
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
    >
      <MonacoCrashBoundary
        resetKey={`${retryNonce}-${monacoLang}-${editorStyle}-${isDark ? "dark" : "light"}`}
        fallback={fallbackEditor}
        onCrash={() => setLoadFailed(true)}
      >
        <Editor
          height={height}
          language={monacoLang}
          theme={pickThemeName()}
          value={value}
          onChange={(v) => onChange?.(v ?? "")}
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
          options={{
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
          }}
        />
      </MonacoCrashBoundary>
    </div>
  );
}
