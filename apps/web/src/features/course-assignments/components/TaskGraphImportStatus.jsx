import React from 'react';
import { getTaskGraphImport } from '../../../api/assignments';
import { Button } from '../../../components/ui';
import { handleApiError } from '../../../utils/handleApiError';
import { downloadTaskGraphAudit, isImportPending } from '../courseTaskGraphOperation';

// Only unresolved imports need a message above the map; completed imports live in the journal.
export default function TaskGraphImportStatus({ importJournal, graphImportRequest, jsonImportBusy,
  prepareMapRecovery, resumeImport, openJsonImportDialog, notify, lastImportDiffRef }) {
  const operation = importJournal.operation;
  if (!isImportPending(operation) || (!importJournal.ready && !importJournal.lookupError)) return null;

  const needsMapRebase = ['map-conflict', 'map-rejected'].includes(operation.state);
  const isSaving = graphImportRequest?.status === 'saving';
  const canContinue = operation.state !== 'missing' && operation.state !== 'sending' && operation.state !== 'unknown';
  const hasKnownResult = Boolean(operation.response);
  const message = operation.state === 'missing'
    ? 'Предыдущий импорт не найден. Откройте исходный JSON.'
    : needsMapRebase ? 'Карта изменилась. Нужно повторно проверить её перед завершением импорта.'
    : isSaving ? 'Данные импортированы, карта сохраняется.'
    : hasKnownResult ? 'Данные импортированы, но сохранение карты не подтверждено.'
    : operation.state === 'sending' ? 'Импорт выполняется. Ожидаем ответ сервера.'
    : 'Состояние импорта пока неизвестно. Проверьте его в журнале.';

  const report = async () => {
    let current = operation;
    try { current = await importJournal.refresh(operation.operationId); } catch {}
    downloadTaskGraphAudit(current, lastImportDiffRef.current?.operationId === current?.operationId ? lastImportDiffRef.current.diff : null);
  };

  return (
    <div className="flex min-w-0 flex-wrap items-center gap-2 text-xs sm:text-sm" role="status" data-testid="import-operation-status">
      <span className="min-w-0 flex-1">{message}</span>
      {importJournal.lookupError || operation.state === 'unknown' || !hasKnownResult ? (
        <Button variant="outline" disabled={importJournal.busy} onClick={() => importJournal.refresh(operation.operationId).catch((error) => handleApiError(error, notify))}>
          Проверить статус
        </Button>
      ) : null}
      {canContinue && !isSaving ? (
        <Button disabled={importJournal.busy || jsonImportBusy} onClick={() => needsMapRebase && hasKnownResult ? prepareMapRecovery() : resumeImport()}>
          {needsMapRebase && hasKnownResult ? 'Пересчитать карту' : hasKnownResult ? 'Завершить импорт' : 'Продолжить импорт'}
        </Button>
      ) : null}
      {operation.state === 'missing' ? <Button variant="outline" onClick={openJsonImportDialog}>Открыть JSON</Button> : null}
      <details className="text-xs text-neutral-500">
        <summary className="cursor-pointer underline underline-offset-2">Журнал</summary>
        <div className="mt-2 flex max-w-lg flex-wrap items-center gap-2">
          <Button variant="outline" onClick={report}>Скачать отчёт</Button>
          {canContinue && !isSaving && !['map-pending', 'missing'].includes(operation.state) ? (
            <button type="button" className="underline" disabled={importJournal.busy} onClick={async () => {
              if (!window.confirm('Прекратить импорт? Уже записанные данные останутся. Это не откат.')) return;
              try { await importJournal.stop(); } catch (error) { handleApiError(error, notify); }
            }}>Прекратить без отката</button>
          ) : null}
          {importJournal.history.length > 1 ? <details className="w-full">
            <summary className="cursor-pointer">История импортов ({importJournal.history.length})</summary>
            <div className="mt-1 max-h-32 overflow-auto">
              {importJournal.history.map((row) => <button type="button" className="block text-left underline" key={row.operationId}
                onClick={() => getTaskGraphImport(operation.courseId, row.operationId).then(downloadTaskGraphAudit).catch((error) => handleApiError(error, notify))}>{row.createdAt} · {row.state}</button>)}
            </div>
          </details> : null}
        </div>
      </details>
      {importJournal.lookupError ? <p className="w-full text-xs text-amber-600 dark:text-amber-300">Не удалось прочитать журнал. Результат импорта ещё неизвестен.</p> : null}
      {operation.error?.message || graphImportRequest?.error ? <p className="w-full text-xs text-red-600 dark:text-red-300">{operation.error?.message || graphImportRequest?.error}</p> : null}
    </div>
  );
}

const importStateNames = {
  completed: 'Готово', stopped: 'Прекращён', 'map-conflict': 'Нужно проверить карту',
  'map-rejected': 'Сохранение карты отклонено', 'map-pending': 'Ожидает карту',
  'data-committed': 'Данные сохранены', sending: 'Выполняется', unknown: 'Статус неизвестен',
};

export function TaskGraphImportHistory({ importJournal, courseId, notify, lastImportDiffRef }) {
  const history = importJournal.history || [];
  if (!history.length) return null;
  return (
    <div className="mt-2 space-y-1 border-b border-[rgba(var(--border)/0.5)] pb-3 text-xs">
      <div className="font-semibold text-current">Импорты</div>
      <div className="max-h-40 space-y-1 overflow-auto">
        {history.map((row) => {
          const date = Date.parse(row.createdAt);
          const label = Number.isFinite(date) ? new Date(date).toLocaleString('ru-RU') : 'Импорт';
          return <div key={row.operationId} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
            <span>{label} · {importStateNames[row.state] || 'Выполняется'}</span>
            <button type="button" className="underline underline-offset-2" onClick={async () => {
              try {
                const result = await getTaskGraphImport(courseId, row.operationId);
                downloadTaskGraphAudit(result, lastImportDiffRef.current?.operationId === result?.operationId ? lastImportDiffRef.current.diff : null);
              } catch (error) { handleApiError(error, notify); }
            }}>Скачать отчёт</button>
          </div>;
        })}
      </div>
    </div>
  );
}
