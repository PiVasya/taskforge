import React from 'react';
import { getTaskGraphImport } from '../../../api/assignments';
import { Button } from '../../../components/ui';
import { handleApiError } from '../../../utils/handleApiError';
import { downloadTaskGraphAudit, isImportPending } from '../courseTaskGraphOperation';

export default function TaskGraphImportStatus({ importJournal, graphImportRequest, jsonImportBusy,
  prepareMapRecovery, resumeImport, openJsonImportDialog, notify, lastImportDiffRef }) {
  return (<>
{(importJournal.operation || importJournal.lookupError) ? (
        <div role="status" className="mb-3 rounded-xl border p-3 text-sm" data-testid="import-operation-status">
          {isImportPending(importJournal.operation) ? <>
            <strong>{'Импорт не завершён.'}</strong>{' '}
            {importJournal.operation?.state === 'map-conflict'
              ? 'Карту изменили после проверки. Пересчитайте и подтвердите изменения.'
              : importJournal.operation?.response
              ? 'Данные записаны. Осталось подтвердить сохранение карты.'
              : 'Результат записи данных ещё не подтверждён.'}
          </> : importJournal.operation?.state === 'stopped' ? <strong>{'Импорт прекращён без отката.'}</strong> : importJournal.operation ? <strong>{'Импорт завершён.'}</strong> : null}
          {importJournal.lookupError ? <p className="my-1">{'Журнал не удалось прочитать. Это не означает откат импорта.'}</p> : null}
          {importJournal.operation?.error?.message ? <p className="my-1">{importJournal.operation.error.message}</p> : null}
          {graphImportRequest?.error ? <p className="my-1">{graphImportRequest.error}</p> : null}
          <div className="mt-2 flex flex-wrap gap-2">
            {importJournal.lookupError || !importJournal.ready ? <Button variant="outline" onClick={() => importJournal.discover()} disabled={importJournal.busy}>
              {'Повторить проверку журнала'}</Button> : null}
            {importJournal.operation ? <Button variant="outline" onClick={() => importJournal.refresh().catch((error) => handleApiError(error, notify))} disabled={importJournal.busy}>
              {'Проверить результат'}</Button> : null}
            {isImportPending(importJournal.operation) && importJournal.operation.state !== 'missing' ? <Button
              disabled={importJournal.busy || jsonImportBusy || graphImportRequest?.status === 'saving'}
              onClick={() => ['map-conflict', 'map-rejected'].includes(importJournal.operation.state) ? prepareMapRecovery() : resumeImport()}>
              {['map-conflict', 'map-rejected'].includes(importJournal.operation.state) ? 'Пересчитать карту' : 'Продолжить этот импорт'}
            </Button> : null}
            {importJournal.operation?.state === 'missing' ? <Button onClick={openJsonImportDialog}>{'Вставить исходный JSON'}</Button> : null}
            {importJournal.operation ? <Button variant="outline" onClick={async () => {
              let op = importJournal.operation;
              try { op = await importJournal.refresh(op.operationId); } catch {}
              downloadTaskGraphAudit(op, lastImportDiffRef.current?.operationId === op?.operationId ? lastImportDiffRef.current.diff : null);
            }}>{'Скачать отчёт'}</Button> : null}
          </div>
          {isImportPending(importJournal.operation) && !['map-pending', 'missing'].includes(importJournal.operation.state) && graphImportRequest?.status !== 'saving' ?
            <button type="button" className="mt-2 text-xs underline" disabled={importJournal.busy} onClick={async () => {
              if (!window.confirm('Прекратить этот импорт? Уже созданные курсы и задания останутся. Это не откат.')) return;
              try { await importJournal.stop(); } catch (error) { handleApiError(error, notify); }
            }}>{'Прекратить без отката'}</button> : null}
          {importJournal.operation?.response ? <details className="mt-2"><summary className="cursor-pointer">{'Результат по объектам'}</summary>
            <div className="max-h-52 overflow-auto text-xs">
              {(importJournal.operation.response.taskGraph?.tasks || []).map((row) => <p key={row.key}>{row.key} : {row.assignmentId} : {({ created: 'Создано', updated: 'Обновлено', unchanged: 'Без изменений' })[row.action] || row.action}</p>)}
              {(importJournal.operation.response.taskGraph?.courses || []).map((row) => <p key={row.key}>{row.title || row.key} : {row.id}</p>)}
              {(importJournal.operation.response.deletedAssignmentIds || []).map((id) => <p key={id}>{'Удалено'}: {id}</p>)}
            </div>
          </details> : null}
          {importJournal.history.length > 1 ? <details className="mt-2"><summary className="cursor-pointer">{'История импортов'}</summary>
            {importJournal.history.map((row) => <button type="button" className="block text-xs underline" key={row.operationId}
              onClick={() => getTaskGraphImport(importJournal.operation.courseId, row.operationId).then((op) => downloadTaskGraphAudit(op)).catch((error) => handleApiError(error, notify))}>{row.createdAt} : {row.state}</button>)}
          </details> : null}
        </div>
      ) : null}  </>);
}
