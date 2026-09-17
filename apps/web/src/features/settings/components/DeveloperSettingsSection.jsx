import React, { useCallback, useEffect, useState } from 'react';
import { Button, Card } from '../../../components/ui';
import {
  clearFrontendDiagnostics,
  downloadFrontendDiagnostics,
  getFrontendDiagnosticsConfig,
  getFrontendDiagnosticsStats,
  setFrontendDiagnosticsEnabled,
  setFrontendDiagnosticsLimitMb,
} from '../../../devtools/frontendDiagnostics';
import { FRONTEND_LOG_LIMIT_CHOICES_MB } from '../../../devtools/frontendDiagnosticsModel';

function formatBytes(value) {
  const bytes = Number(value || 0);
  if (bytes < 1024) return `${bytes} Б`;
  const units = ['КБ', 'МБ', 'ГБ'];
  let size = bytes / 1024;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index += 1;
  }
  return `${size >= 100 ? size.toFixed(0) : size >= 10 ? size.toFixed(1) : size.toFixed(2)} ${units[index]}`;
}

function DeveloperModeSwitch({ enabled, onChange }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={enabled}
      aria-label="Режим разработчика"
      onClick={() => onChange(!enabled)}
      className={`tf-style-switch ${enabled ? 'is-on' : ''}`}
    >
      <span className="tf-style-switch__thumb" />
    </button>
  );
}

export default function DeveloperSettingsSection({ notify }) {
  const [config, setConfig] = useState(() => getFrontendDiagnosticsConfig());
  const [stats, setStats] = useState(() => ({ count: 0, bytes: 0, queued: 0, droppedInMemory: 0, suppressedByRateLimit: 0, storageError: '' }));
  const [busy, setBusy] = useState('');

  const refreshStats = useCallback(async () => {
    const next = await getFrontendDiagnosticsStats();
    setStats(next);
    setConfig({ enabled: next.enabled, limitMb: next.limitMb });
  }, []);

  useEffect(() => {
    refreshStats();
    const timer = window.setInterval(refreshStats, config.enabled ? 2000 : 5000);
    const onStorage = () => {
      setConfig(getFrontendDiagnosticsConfig());
      refreshStats();
    };
    window.addEventListener('storage', onStorage);
    window.addEventListener('tf-frontend-diagnostics-config', onStorage);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('storage', onStorage);
      window.removeEventListener('tf-frontend-diagnostics-config', onStorage);
    };
  }, [config.enabled, refreshStats]);

  const toggleDeveloperMode = useCallback((enabled) => {
    setFrontendDiagnosticsEnabled(enabled);
    setConfig((previous) => ({ ...previous, enabled }));
    notify?.success?.(enabled ? 'Режим разработчика включён' : 'Режим разработчика выключен');
  }, [notify]);

  const changeLimit = useCallback((limitMb) => {
    setFrontendDiagnosticsLimitMb(limitMb);
    setConfig((previous) => ({ ...previous, limitMb }));
    notify?.success?.(`Лимит фронтенд-логов: ${limitMb} МБ`);
  }, [notify]);

  const download = useCallback(async () => {
    try {
      setBusy('download');
      const result = await downloadFrontendDiagnostics();
      notify?.success?.(`Логи подготовлены: ${result.count || 0} событий`);
      await refreshStats();
    } catch (error) {
      notify?.error?.(error?.message || 'Не удалось скачать логи фронтенда');
    } finally {
      setBusy('');
    }
  }, [notify, refreshStats]);

  const clear = useCallback(async () => {
    if (!window.confirm('Очистить все локальные логи фронтенда на этом устройстве?')) return;
    try {
      setBusy('clear');
      await clearFrontendDiagnostics();
      await refreshStats();
      notify?.success?.('Логи фронтенда очищены');
    } catch (error) {
      notify?.error?.(error?.message || 'Не удалось очистить логи фронтенда');
    } finally {
      setBusy('');
    }
  }, [notify, refreshStats]);

  const limitBytes = Number(config.limitMb || 0) * 1024 * 1024;
  const usedPercent = limitBytes > 0 ? Math.min(100, Math.round((Number(stats.bytes || 0) / limitBytes) * 100)) : 0;

  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="min-w-0">
            <div className="font-semibold">Режим разработчика</div>
            <div className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
              Подробно записывает работу фронтенда на этом устройстве. Логи не отправляются на сервер автоматически.
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-3">
            <span className="text-sm font-semibold">{config.enabled ? 'Включён' : 'Выключен'}</span>
            <DeveloperModeSwitch enabled={config.enabled} onChange={toggleDeveloperMode} />
          </div>
        </div>
      </Card>

      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Хранилище фронтенд-логов</div>
          <div className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
            IndexedDB, кольцевой буфер. При достижении лимита самые старые события удаляются автоматически.
          </div>
        </div>

        <div className="grid grid-cols-2 gap-2 sm:grid-cols-5" role="group" aria-label="Лимит фронтенд-логов">
          {FRONTEND_LOG_LIMIT_CHOICES_MB.map((limit) => (
            <Button
              key={limit}
              type="button"
              variant={config.limitMb === limit ? 'primary' : 'outline'}
              onClick={() => changeLimit(limit)}
            >
              {limit} МБ
            </Button>
          ))}
        </div>

        <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--card)/0.5)] p-3">
          <div className="flex items-center justify-between gap-3 text-sm">
            <span>Занято</span>
            <span className="font-semibold">{formatBytes(stats.bytes)} / {config.limitMb} МБ</span>
          </div>
          <div className="mt-2 h-2 overflow-hidden rounded-full bg-[rgba(var(--border)/0.25)]">
            <div className="h-full rounded-full bg-[rgb(var(--accent))] transition-[width]" style={{ width: `${usedPercent}%` }} />
          </div>
          <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-neutral-500 dark:text-neutral-400">
            <span>Событий: {Number(stats.count || 0).toLocaleString('ru-RU')}</span>
            {stats.queued ? <span>В очереди записи: {stats.queued}</span> : null}
            {stats.droppedInMemory ? <span>Отброшено при перегрузке: {stats.droppedInMemory}</span> : null}
            {stats.suppressedByRateLimit ? <span>Подавлено при шторме: {Number(stats.suppressedByRateLimit).toLocaleString('ru-RU')}</span> : null}
          </div>
          {stats.storageError ? (
            <div className="mt-2 text-xs text-amber-600 dark:text-amber-300">Последняя ошибка хранилища: {stats.storageError}</div>
          ) : null}
        </div>

        <div className="flex flex-wrap gap-2">
          <Button type="button" onClick={download} disabled={busy !== '' || Number(stats.count || 0) === 0}>
            {busy === 'download' ? 'Подготовка…' : 'Скачать все логи фронтенда'}
          </Button>
          <Button type="button" variant="outline" onClick={clear} disabled={busy !== '' || (Number(stats.count || 0) === 0 && !stats.queued)}>
            {busy === 'clear' ? 'Очистка…' : 'Очистить логи'}
          </Button>
        </div>
      </Card>

      <Card className="p-4 space-y-2 text-sm text-neutral-600 dark:text-neutral-300">
        <div className="font-semibold text-[rgb(var(--text))]">Что попадает в лог</div>
        <div>Переходы между страницами, API-запросы и ответы, ошибки, query-cache, realtime-подключения, клики и формы, долгие задачи браузера, медленные ресурсы и доступные метрики памяти.</div>
        <div>Пароли, токены, cookies, email/телефон, исходный код решений и ответы заданий автоматически вырезаются.</div>
      </Card>
    </div>
  );
}
