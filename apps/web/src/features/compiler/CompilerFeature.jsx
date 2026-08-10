import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Braces,
  Check,
  ChevronDown,
  CircleStop,
  Clipboard,
  Download,
  Eraser,
  Expand,
  FileCode2,
  Play,
  RotateCcw,
  Shrink,
  Terminal,
} from 'lucide-react';
import CodeEditor from '../../components/CodeEditor';
import { createInteractiveCompilerSession } from '../../api/compiler';
import InteractiveTerminal from './InteractiveTerminal';
import './compiler.css';
import { useNotify } from '../../components/notify/NotifyProvider';
import useSaveShortcut from '../../hooks/useSaveShortcut';

const STORAGE_LANGUAGE = 'taskforge.compiler.language.v1';
const STORAGE_DRAFT_PREFIX = 'taskforge.compiler.draft.v1.';

const LANGUAGES = [
  {
    id: 'cpp',
    label: 'C++',
    runtime: 'GCC',
    fileName: 'main.cpp',
    template: `#include <iostream>\n#include <string>\nusing namespace std;\n\nint main() {\n    cout << "Введите имя: " << flush;\n\n    string name;\n    getline(cin, name);\n\n    cout << "Привет, " << name << "!" << endl;\n    return 0;\n}\n`,
  },
  {
    id: 'csharp',
    label: 'C#',
    runtime: '.NET',
    fileName: 'Program.cs',
    template: `using System;\n\nConsole.Write("Введите имя: ");\nstring name = Console.ReadLine() ?? "";\nConsole.WriteLine($"Привет, {name}!");\n`,
  },
  {
    id: 'java',
    label: 'Java',
    runtime: 'OpenJDK',
    fileName: 'Main.java',
    template: `import java.util.Scanner;\n\npublic class Main {\n    public static void main(String[] args) {\n        Scanner scanner = new Scanner(System.in);\n\n        System.out.print("Введите имя: ");\n        String name = scanner.nextLine();\n\n        System.out.println("Привет, " + name + "!");\n    }\n}\n`,
  },
  {
    id: 'python',
    label: 'Python',
    runtime: 'CPython',
    fileName: 'main.py',
    template: `name = input("Введите имя: ")\nprint(f"Привет, {name}!")\n`,
  },
  {
    id: 'pascal',
    label: 'Pascal',
    runtime: 'Free Pascal',
    fileName: 'main.pas',
    template: `program HelloTaskForge;\n\nvar\n  Name: string;\n\nbegin\n  Write('Введите имя: ');\n  ReadLn(Name);\n  WriteLn('Привет, ', Name, '!');\nend.\n`,
  },
];

const LANGUAGE_MAP = Object.fromEntries(LANGUAGES.map((language) => [language.id, language]));

function readStoredLanguage() {
  try {
    const value = window.localStorage.getItem(STORAGE_LANGUAGE);
    return LANGUAGE_MAP[value] ? value : 'cpp';
  } catch {
    return 'cpp';
  }
}

function readDraft(language) {
  const fallback = LANGUAGE_MAP[language]?.template || '';
  try {
    const value = window.localStorage.getItem(`${STORAGE_DRAFT_PREFIX}${language}`);
    return value === null ? fallback : value;
  } catch {
    return fallback;
  }
}

function saveDraft(language, code) {
  try {
    window.localStorage.setItem(`${STORAGE_DRAFT_PREFIX}${language}`, code);
    window.localStorage.setItem(STORAGE_LANGUAGE, language);
  } catch {}
}

function socketUrl(relativeUrl) {
  const url = new URL(relativeUrl, window.location.origin);
  url.protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.toString();
}

function apiMessage(error) {
  const data = error?.response?.data;
  if (typeof data === 'string' && data.trim()) {
    try {
      const parsed = JSON.parse(data);
      return parsed?.message || parsed?.error || data;
    } catch {
      return data;
    }
  }
  return data?.message || data?.error || error?.message || 'Не удалось запустить компилятор.';
}

function statusText(status, exitInfo) {
  if (status === 'creating') return 'Проверка кода';
  if (status === 'connecting') return 'Подключение';
  if (status === 'compiling') return 'Компиляция';
  if (status === 'running') return 'Программа работает';
  if (status === 'stopping') return 'Остановка';
  if (status === 'error') return 'Ошибка';
  if (status === 'finished') {
    if (!exitInfo) return 'Сессия закрыта';
    if (exitInfo.reason === 'time_limit') return 'Лимит времени';
    if (exitInfo.reason === 'output_limit') return 'Лимит вывода';
    return exitInfo.exitCode === 0 ? 'Завершено' : `Код выхода ${exitInfo.exitCode}`;
  }
  return 'Готово к запуску';
}

function CompilerFeature() {
  const notify = useNotify();
  const [language, setLanguage] = useState(readStoredLanguage);
  const [code, setCode] = useState(() => readDraft(readStoredLanguage()));
  const [status, setStatus] = useState('idle');
  const [exitInfo, setExitInfo] = useState(null);
  const [dimensions, setDimensions] = useState({ columns: 110, rows: 30 });
  const [editorPercent, setEditorPercent] = useState(55);
  const [mobileTab, setMobileTab] = useState('code');
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [languageMenuOpen, setLanguageMenuOpen] = useState(false);

  const pageRef = useRef(null);
  const languageSelectRef = useRef(null);
  const workspaceRef = useRef(null);
  const terminalRef = useRef(null);
  const socketRef = useRef(null);
  const runSequenceRef = useRef(0);
  const terminalStatusRef = useRef('idle');
  const terminalOutcomeRef = useRef(false);

  const selectedLanguage = LANGUAGE_MAP[language] || LANGUAGES[0];
  const active = ['creating', 'connecting', 'compiling', 'running', 'stopping'].includes(status);
  const acceptingInput = status === 'running' && socketRef.current?.readyState === WebSocket.OPEN;

  useEffect(() => {
    const timer = window.setTimeout(() => saveDraft(language, code), 250);
    return () => window.clearTimeout(timer);
  }, [code, language]);

  const saveCurrentDraft = useCallback(() => {
    saveDraft(language, code);
    notify.success('Черновик кода сохранён локально');
  }, [code, language, notify]);

  useSaveShortcut(saveCurrentDraft);

  useEffect(() => {
    const handleFullscreen = () => setIsFullscreen(document.fullscreenElement === pageRef.current);
    document.addEventListener('fullscreenchange', handleFullscreen);
    return () => document.removeEventListener('fullscreenchange', handleFullscreen);
  }, []);


  useEffect(() => {
    if (!languageMenuOpen) return undefined;
    const handlePointerDown = (event) => {
      if (!languageSelectRef.current?.contains(event.target)) setLanguageMenuOpen(false);
    };
    const handleKeyDown = (event) => {
      if (event.key === 'Escape') setLanguageMenuOpen(false);
    };
    document.addEventListener('pointerdown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [languageMenuOpen]);

  useEffect(() => () => {
    runSequenceRef.current += 1;
    const socket = socketRef.current;
    if (socket?.readyState === WebSocket.OPEN) {
      try { socket.send(JSON.stringify({ type: 'stop' })); } catch {}
    }
    try { socket?.close(); } catch {}
  }, []);

  const changeLanguage = useCallback((nextLanguage) => {
    if (!LANGUAGE_MAP[nextLanguage]) return;
    setLanguageMenuOpen(false);
    if (nextLanguage === language) return;
    saveDraft(language, code);
    setLanguage(nextLanguage);
    setCode(readDraft(nextLanguage));
    setExitInfo(null);
  }, [code, language]);

  const sendSocketMessage = useCallback((payload) => {
    const socket = socketRef.current;
    if (socket?.readyState !== WebSocket.OPEN) return false;
    try {
      socket.send(JSON.stringify(payload));
      return true;
    } catch {
      return false;
    }
  }, []);

  const closeCurrentSocket = useCallback((sendStop = false) => {
    const socket = socketRef.current;
    socketRef.current = null;
    if (!socket) return;
    if (sendStop && socket.readyState === WebSocket.OPEN) {
      try { socket.send(JSON.stringify({ type: 'stop' })); } catch {}
    }
    try { socket.close(1000, 'new session'); } catch {}
  }, []);

  const start = useCallback(async ({ preserveConsole = false } = {}) => {
    if (!code.trim()) {
      terminalRef.current?.clear();
      terminalRef.current?.writeSystem('[TaskForge] Введите код перед запуском.');
      setStatus('error');
      return;
    }

    const sequence = runSequenceRef.current + 1;
    runSequenceRef.current = sequence;
    closeCurrentSocket(true);
    terminalStatusRef.current = 'idle';
    terminalOutcomeRef.current = false;
    if (!preserveConsole) terminalRef.current?.clear();
    terminalRef.current?.writeSystem(`[TaskForge] ${selectedLanguage.label} · ${selectedLanguage.runtime}`);
    terminalRef.current?.writeSystem('[TaskForge] Проверка безопасности и подготовка сессии...');
    setExitInfo(null);
    setStatus('creating');
    setMobileTab('console');

    try {
      const session = await createInteractiveCompilerSession({
        language,
        code,
        columns: dimensions.columns,
        rows: dimensions.rows,
        timeLimitMs: 120000,
        memoryLimitMb: 256,
      });
      if (sequence !== runSequenceRef.current) return;

      setStatus('connecting');
      const socket = new WebSocket(socketUrl(session.websocketUrl));
      socketRef.current = socket;

      socket.addEventListener('open', () => {
        if (sequence !== runSequenceRef.current) {
          socket.close();
          return;
        }
        terminalRef.current?.focus();
      });

      socket.addEventListener('message', (event) => {
        if (sequence !== runSequenceRef.current) return;
        let message;
        try { message = JSON.parse(event.data); } catch { return; }

        if (message.type === 'output') {
          terminalRef.current?.write(message.data || '');
          return;
        }
        if (message.type === 'status') {
          const phase = message.phase === 'running' ? 'running' : 'compiling';
          setStatus(phase);
          if (terminalStatusRef.current !== phase) {
            terminalStatusRef.current = phase;
            terminalRef.current?.writeSystem(
              phase === 'running'
                ? '[TaskForge] Компиляция завершена. Программа запущена.'
                : '[TaskForge] Компиляция...',
            );
          }
          if (phase === 'running') terminalRef.current?.focus();
          return;
        }
        if (message.type === 'exit') {
          terminalOutcomeRef.current = true;
          const nextExitInfo = {
            exitCode: Number(message.exitCode ?? 0),
            reason: message.reason || 'completed',
            durationMs: Number(message.durationMs || 0),
          };
          setExitInfo(nextExitInfo);
          setStatus('finished');
          const duration = nextExitInfo.durationMs > 0 ? ` · ${nextExitInfo.durationMs} мс` : '';
          terminalRef.current?.writeSystem(
            `[TaskForge] Процесс завершён: код ${nextExitInfo.exitCode}${duration}.`,
          );
          return;
        }
        if (message.type === 'error') {
          terminalOutcomeRef.current = true;
          setStatus('error');
          terminalRef.current?.writeSystem(`[TaskForge] ${message.message || 'Сессия завершилась с ошибкой.'}`);
        }
      });

      socket.addEventListener('error', () => {
        if (sequence !== runSequenceRef.current || terminalOutcomeRef.current) return;
        terminalOutcomeRef.current = true;
        setStatus('error');
        terminalRef.current?.writeSystem('[TaskForge] Соединение с консолью прервано.');
      });

      socket.addEventListener('close', () => {
        if (socketRef.current === socket) socketRef.current = null;
        if (sequence !== runSequenceRef.current) return;
        setStatus((current) => {
          if (terminalOutcomeRef.current || current === 'finished' || current === 'error' || current === 'idle') return current;
          terminalOutcomeRef.current = true;
          terminalRef.current?.writeSystem('[TaskForge] Сессия закрылась до запуска программы. Проверьте раннер и ключ безопасности.');
          return 'error';
        });
      });
    } catch (error) {
      if (sequence !== runSequenceRef.current) return;
      setStatus('error');
      terminalRef.current?.writeSystem(`[TaskForge] ${apiMessage(error)}`);
    }
  }, [closeCurrentSocket, code, dimensions.columns, dimensions.rows, language, selectedLanguage.label, selectedLanguage.runtime]);

  const stop = useCallback(() => {
    if (!active) return;
    setStatus('stopping');
    terminalRef.current?.writeSystem('[TaskForge] Остановка процесса...');
    if (!sendSocketMessage({ type: 'stop' })) {
      runSequenceRef.current += 1;
      closeCurrentSocket(false);
      setStatus('finished');
    }
  }, [active, closeCurrentSocket, sendSocketMessage]);

  const restart = useCallback(() => {
    runSequenceRef.current += 1;
    closeCurrentSocket(true);
    window.setTimeout(() => start(), 120);
  }, [closeCurrentSocket, start]);

  const resetTemplate = useCallback(() => {
    setCode(selectedLanguage.template);
  }, [selectedLanguage.template]);

  const downloadSource = useCallback(() => {
    const blob = new Blob([code], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = selectedLanguage.fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }, [code, selectedLanguage.fileName]);

  const copyConsole = useCallback(async () => {
    const text = terminalRef.current?.getText() || '';
    try {
      await navigator.clipboard.writeText(text);
      terminalRef.current?.writeSystem('[TaskForge] Вывод скопирован.');
    } catch {
      terminalRef.current?.writeSystem('[TaskForge] Не удалось скопировать вывод.');
    }
  }, []);

  const toggleFullscreen = useCallback(async () => {
    try {
      if (document.fullscreenElement === pageRef.current) await document.exitFullscreen();
      else await pageRef.current?.requestFullscreen();
    } catch {}
  }, []);

  const handleTerminalInput = useCallback((data) => {
    sendSocketMessage({ type: 'input', data });
  }, [sendSocketMessage]);

  const handleInterrupt = useCallback(() => {
    if (sendSocketMessage({ type: 'interrupt' })) {
      terminalRef.current?.writeSystem('^C');
    }
  }, [sendSocketMessage]);

  const handleTerminalResize = useCallback((nextDimensions) => {
    setDimensions(nextDimensions);
    sendSocketMessage({
      type: 'resize',
      columns: nextDimensions.columns,
      rows: nextDimensions.rows,
    });
  }, [sendSocketMessage]);

  const beginResize = useCallback((event) => {
    if (!workspaceRef.current || window.matchMedia('(max-width: 899px)').matches) return;
    event.preventDefault();
    const workspace = workspaceRef.current;
    const rect = workspace.getBoundingClientRect();
    const pointerId = event.pointerId;
    event.currentTarget.setPointerCapture?.(pointerId);

    const move = (moveEvent) => {
      const percent = ((moveEvent.clientX - rect.left) / rect.width) * 100;
      setEditorPercent(Math.min(75, Math.max(28, percent)));
    };
    const finish = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish, { once: true });
    window.addEventListener('pointercancel', finish, { once: true });
  }, []);

  const statusLabel = useMemo(() => statusText(status, exitInfo), [exitInfo, status]);
  const statusTone = useMemo(() => {
    if (status !== 'finished' || !exitInfo) return status;
    if (exitInfo.reason === 'time_limit' || exitInfo.reason === 'output_limit') return 'limited';
    return exitInfo.exitCode === 0 ? 'finished' : 'failed';
  }, [exitInfo, status]);

  return (
    <div ref={pageRef} className={`compiler-page ${isFullscreen ? 'is-fullscreen' : ''}`}>
      <div className="compiler-page__hero">
        <div className="compiler-page__heading">
          <div className="compiler-page__mark"><Braces size={24} /></div>
          <div>
            <h1>Онлайн-компилятор</h1>
            <p>Пишите код и общайтесь с программой через живую интерактивную консоль.</p>
          </div>
        </div>
        <div className={`compiler-status is-${statusTone}`}>
          <span className="compiler-status__dot" />
          <span>{statusLabel}</span>
          {exitInfo?.durationMs > 0 ? <small>{exitInfo.durationMs} мс</small> : null}
        </div>
      </div>

      <div className="compiler-toolbar card">
        <div ref={languageSelectRef} className={`compiler-language-select ${languageMenuOpen ? 'is-open' : ''}`}>
          <button
            type="button"
            className="compiler-language-select__trigger"
            onClick={() => setLanguageMenuOpen((open) => !open)}
            disabled={active}
            aria-haspopup="listbox"
            aria-expanded={languageMenuOpen}
          >
            <FileCode2 size={17} />
            <span>{selectedLanguage.label} · {selectedLanguage.runtime}</span>
            <ChevronDown size={15} aria-hidden="true" />
          </button>
          {languageMenuOpen ? (
            <div className="compiler-language-select__menu" role="listbox" aria-label="Язык компилятора">
              {LANGUAGES.map((item) => {
                const selected = item.id === language;
                return (
                  <button
                    key={item.id}
                    type="button"
                    role="option"
                    aria-selected={selected}
                    className={`compiler-language-select__option ${selected ? 'is-selected' : ''}`}
                    onClick={() => changeLanguage(item.id)}
                  >
                    <span className="compiler-language-select__option-mark">
                      {selected ? <Check size={14} strokeWidth={2.4} /> : null}
                    </span>
                    <span>{item.label}</span>
                    <small>{item.runtime}</small>
                  </button>
                );
              })}
            </div>
          ) : null}
        </div>

        <div className="compiler-toolbar__meta">
          <span>{selectedLanguage.fileName}</span>
          <span>{dimensions.columns}×{dimensions.rows}</span>
          <span>до 120 сек.</span>
        </div>

        <div className="compiler-toolbar__actions">
          <button type="button" className="compiler-button" onClick={resetTemplate} disabled={active} title="Вернуть стартовый шаблон">
            <RotateCcw size={17} /><span>Шаблон</span>
          </button>
          <button type="button" className="compiler-button" onClick={downloadSource} title="Скачать исходник">
            <Download size={17} /><span>Скачать</span>
          </button>
          <button type="button" className="compiler-button compiler-button--icon" onClick={toggleFullscreen} title="Полноэкранный режим">
            {isFullscreen ? <Shrink size={18} /> : <Expand size={18} />}
          </button>
          {active ? (
            <button type="button" className="compiler-button compiler-button--stop" onClick={stop}>
              <CircleStop size={18} /><span>Остановить</span>
            </button>
          ) : (
            <button type="button" className="compiler-button compiler-button--run" onClick={() => start()}>
              <Play size={18} fill="currentColor" /><span>Запустить</span>
            </button>
          )}
          <button type="button" className="compiler-button compiler-button--icon" onClick={restart} disabled={status === 'creating'} title="Перезапустить">
            <RotateCcw size={18} />
          </button>
        </div>
      </div>

      <div className="compiler-mobile-tabs" role="tablist" aria-label="Область компилятора">
        <button type="button" className={mobileTab === 'code' ? 'is-active' : ''} onClick={() => setMobileTab('code')}>
          <FileCode2 size={16} /> Код
        </button>
        <button type="button" className={mobileTab === 'console' ? 'is-active' : ''} onClick={() => setMobileTab('console')}>
          <Terminal size={16} /> Консоль
        </button>
      </div>

      <div
        ref={workspaceRef}
        className="compiler-workspace card"
        style={{ '--compiler-editor-percent': `${editorPercent}%` }}
      >
        <section className={`compiler-pane compiler-pane--editor ${mobileTab === 'code' ? 'is-mobile-active' : ''}`}>
          <header className="compiler-pane__header">
            <div><FileCode2 size={16} /><span>{selectedLanguage.fileName}</span></div>
            <span className="compiler-pane__hint">Черновик сохраняется автоматически</span>
          </header>
          <div className="compiler-editor-host">
            <CodeEditor
              language={language}
              value={code}
              onChange={setCode}
              modelPath={`taskforge-compiler://${language}/${selectedLanguage.fileName}`}
              height="100%"
            />
          </div>
        </section>

        <button
          type="button"
          className="compiler-splitter"
          onPointerDown={beginResize}
          aria-label="Изменить ширину редактора и консоли"
          title="Потяните, чтобы изменить размер панелей"
        ><span /></button>

        <section className={`compiler-pane compiler-pane--terminal ${mobileTab === 'console' ? 'is-mobile-active' : ''}`}>
          <header className="compiler-pane__header compiler-pane__header--terminal">
            <div><Terminal size={16} /><span>Консоль</span></div>
            <div className="compiler-console-actions">
              <button type="button" onClick={copyConsole} title="Скопировать вывод"><Clipboard size={15} /></button>
              <button type="button" onClick={() => terminalRef.current?.clear()} title="Очистить консоль"><Eraser size={15} /></button>
            </div>
          </header>
          <InteractiveTerminal
            ref={terminalRef}
            acceptingInput={acceptingInput}
            onInput={handleTerminalInput}
            onInterrupt={handleInterrupt}
            onResize={handleTerminalResize}
          />
          <footer className="compiler-terminal-footer">
            <span>{acceptingInput ? 'Ввод активен' : 'Ввод появится после запуска'}</span>
            <span>Enter · стрелки · Backspace · Ctrl+C</span>
          </footer>
        </section>
      </div>

      <div className="compiler-note">
        <Terminal size={16} />
        <span>Это консоль выполняемой программы, а не доступ к оболочке сервера. JavaScript намеренно вынесен из списка — для него будет отдельная среда фронтенд-задач.</span>
      </div>
    </div>
  );
}

export default CompilerFeature;
