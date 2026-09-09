import React, {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from 'react';
import { TerminalScreenModel } from './terminalModel';

const MIN_COLUMNS = 40;
const MAX_COLUMNS = 240;
const MIN_ROWS = 10;
const MAX_ROWS = 80;

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

const InteractiveTerminal = forwardRef(function InteractiveTerminal(
  { onInput, onInterrupt, onResize, acceptingInput = false, className = '' },
  forwardedRef,
) {
  const modelRef = useRef(new TerminalScreenModel());
  const viewportRef = useRef(null);
  const inputRef = useRef(null);
  const frameRef = useRef(0);
  const followOutputRef = useRef(true);
  const [revision, setRevision] = useState(0);
  const [dimensions, setDimensions] = useState({ columns: 110, rows: 30 });

  const scheduleRender = useCallback(() => {
    if (frameRef.current) return;
    frameRef.current = window.requestAnimationFrame(() => {
      frameRef.current = 0;
      setRevision((value) => value + 1);
    });
  }, []);

  const focusInput = useCallback(() => {
    if (!acceptingInput) return;
    inputRef.current?.focus({ preventScroll: true });
  }, [acceptingInput]);

  useImperativeHandle(forwardedRef, () => ({
    write(data) {
      modelRef.current.write(data);
      scheduleRender();
    },
    writeSystem(message) {
      modelRef.current.appendLine(`\u001b[2m${String(message || '')}\u001b[0m`);
      scheduleRender();
    },
    clear() {
      modelRef.current.clear();
      followOutputRef.current = true;
      scheduleRender();
    },
    focus: focusInput,
    getDimensions() {
      return dimensions;
    },
    getText() {
      return modelRef.current.snapshot().text;
    },
  }), [dimensions, focusInput, scheduleRender]);

  useEffect(() => () => {
    if (frameRef.current) window.cancelAnimationFrame(frameRef.current);
  }, []);

  useEffect(() => {
    const element = viewportRef.current;
    if (!element || typeof ResizeObserver === 'undefined') return undefined;

    const update = () => {
      const width = Math.max(1, element.clientWidth - 30);
      const height = Math.max(1, element.clientHeight - 28);
      const next = {
        columns: clamp(Math.floor(width / 8.35), MIN_COLUMNS, MAX_COLUMNS),
        rows: clamp(Math.floor(height / 19.5), MIN_ROWS, MAX_ROWS),
      };
      setDimensions((current) => {
        if (current.columns === next.columns && current.rows === next.rows) return current;
        onResize?.(next);
        return next;
      });
    };

    const observer = new ResizeObserver(update);
    observer.observe(element);
    update();
    return () => observer.disconnect();
  }, [onResize]);

  useEffect(() => {
    if (!followOutputRef.current) return;
    const viewport = viewportRef.current;
    if (!viewport) return;
    viewport.scrollTop = viewport.scrollHeight;
  }, [revision]);

  const snapshot = modelRef.current.snapshot();

  const handleScroll = useCallback(() => {
    const viewport = viewportRef.current;
    if (!viewport) return;
    followOutputRef.current = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 72;
  }, []);

  const sendText = useCallback((value) => {
    if (!acceptingInput || !value) return;
    onInput?.(value);
  }, [acceptingInput, onInput]);

  const handleKeyDown = useCallback((event) => {
    if (!acceptingInput) return;

    const selected = window.getSelection()?.toString();
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') {
      if (selected) return;
      event.preventDefault();
      onInterrupt?.();
      return;
    }

    if (event.ctrlKey || event.metaKey) {
      const controlMap = {
        d: '\u0004',
        l: '\u000c',
        z: '\u001a',
      };
      const mapped = controlMap[event.key.toLowerCase()];
      if (mapped) {
        event.preventDefault();
        sendText(mapped);
      }
      return;
    }

    const specialKeys = {
      Enter: '\r',
      Backspace: '\u007f',
      Tab: '\t',
      Escape: '\u001b',
      ArrowUp: '\u001b[A',
      ArrowDown: '\u001b[B',
      ArrowRight: '\u001b[C',
      ArrowLeft: '\u001b[D',
      Home: '\u001b[H',
      End: '\u001b[F',
      Delete: '\u001b[3~',
      PageUp: '\u001b[5~',
      PageDown: '\u001b[6~',
      Insert: '\u001b[2~',
    };
    const sequence = specialKeys[event.key];
    if (sequence) {
      event.preventDefault();
      sendText(sequence);
    }
  }, [acceptingInput, onInterrupt, sendText]);

  const handleInput = useCallback((event) => {
    const value = event.currentTarget.value;
    event.currentTarget.value = '';
    sendText(value);
  }, [sendText]);

  const handleClick = useCallback(() => {
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed) return;
    focusInput();
  }, [focusInput]);

  return (
    <div
      ref={viewportRef}
      className={`tf-terminal ${acceptingInput ? 'is-interactive' : ''} ${className}`}
      onScroll={handleScroll}
      onClick={handleClick}
      role="application"
      aria-label="Интерактивная консоль программы"
      tabIndex={-1}
    >
      <pre className="tf-terminal__screen" aria-live="polite" aria-atomic="false">
        {snapshot.before}
        <span
          className={`tf-terminal__cursor ${snapshot.cursorVisible && acceptingInput ? 'is-visible' : ''}`}
          aria-hidden="true"
        >
          {snapshot.after.startsWith('\n') || snapshot.after.length === 0 ? ' ' : snapshot.after[0]}
        </span>
        {snapshot.after.startsWith('\n') || snapshot.after.length === 0 ? snapshot.after : snapshot.after.slice(1)}
      </pre>
      <textarea
        ref={inputRef}
        className="tf-terminal__input-proxy"
        aria-label="Ввод в консоль"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        disabled={!acceptingInput}
        onKeyDown={handleKeyDown}
        onInput={handleInput}
      />
      {!acceptingInput && snapshot.text.length === 0 ? (
        <div className="tf-terminal__empty">Запустите программу — вывод появится здесь</div>
      ) : null}
    </div>
  );
});

export default React.memo(InteractiveTerminal);
