import React from 'react';
import { Button } from '../../../components/ui';
import { isTransferActive, transferImportPercent } from '../graphTransferModel';

const text = {
  awaitingMap: "\u041e\u0436\u0438\u0434\u0430\u043d\u0438\u0435 \u043a\u0430\u0440\u0442\u044b",
  snapshot: '\u0421\u043d\u0438\u043c\u043e\u043a \u0434\u043b\u044f Preview',
  heading: '\u0418\u043c\u043f\u043e\u0440\u0442 \u0438 \u044d\u043a\u0441\u043f\u043e\u0440\u0442',
  export: '\u042d\u043a\u0441\u043f\u043e\u0440\u0442', preview: '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 JSON',
  import: '\u0418\u043c\u043f\u043e\u0440\u0442', resume: '\u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0435\u043d\u0438\u0435 \u0438\u043c\u043f\u043e\u0440\u0442\u0430',
  map: '\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435 \u043a\u0430\u0440\u0442\u044b',
  queued: '\u0412 \u043e\u0447\u0435\u0440\u0435\u0434\u0438', running: '\u0412\u044b\u043f\u043e\u043b\u043d\u044f\u0435\u0442\u0441\u044f',
  succeeded: '\u0413\u043e\u0442\u043e\u0432\u043e', failed: '\u041e\u0448\u0438\u0431\u043a\u0430', cancelled: '\u041e\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u043e',
  expired: '\u0421\u0440\u043e\u043a \u0445\u0440\u0430\u043d\u0435\u043d\u0438\u044f \u0438\u0441\u0442\u0451\u043a',
  stop: '\u041e\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c', retry: '\u041f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u044c', download: '\u0421\u043a\u0430\u0447\u0430\u0442\u044c JSON',
  status: '\u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0442\u0443\u0441', history: '\u0418\u0441\u0442\u043e\u0440\u0438\u044f \u043e\u043f\u0435\u0440\u0430\u0446\u0438\u0439',
  metadata: '\u041b\u0451\u0433\u043a\u0438\u0439 \u0440\u0435\u0436\u0438\u043c', content: '\u0421\u043e\u0434\u0435\u0440\u0436\u0438\u043c\u043e\u0435',
  pendingMap: '\u0414\u0430\u043d\u043d\u044b\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u044b; \u043e\u0436\u0438\u0434\u0430\u0435\u0442\u0441\u044f \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d\u0438\u0435 \u043a\u0430\u0440\u0442\u044b \u0432 \u0436\u0443\u0440\u043d\u0430\u043b\u0435 \u0438\u043c\u043f\u043e\u0440\u0442\u0430.',
  unavailable: '\u0421\u0442\u0430\u0442\u0443\u0441 \u0432\u0440\u0435\u043c\u0435\u043d\u043d\u043e \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d. \u041e\u043f\u0435\u0440\u0430\u0446\u0438\u044f \u043d\u0430 \u0441\u0435\u0440\u0432\u0435\u0440\u0435 \u043c\u043e\u0436\u0435\u0442 \u043f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u0442\u044c\u0441\u044f.',
  sending: '\u041f\u0435\u0440\u0435\u0434\u0430\u0447\u0430 \u0437\u0430\u043f\u0440\u043e\u0441\u0430 \u0438 \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043a\u0430 \u043f\u0440\u043e\u0441\u043c\u043e\u0442\u0440\u0430\u2026',
  noRollback: '\u0423\u0436\u0435 \u0437\u0430\u043f\u0438\u0441\u0430\u043d\u043d\u044b\u0435 \u0434\u0430\u043d\u043d\u044b\u0435 \u043d\u0435 \u043e\u0442\u043a\u0430\u0442\u044b\u0432\u0430\u044e\u0442\u0441\u044f. \u041f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435 \u0436\u0443\u0440\u043d\u0430\u043b \u0438\u043c\u043f\u043e\u0440\u0442\u0430.',
  stopping: '\u041e\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0437\u0430\u043f\u0440\u043e\u0448\u0435\u043d\u0430',
  closed: '\u0417\u0430\u043a\u0440\u044b\u0442\u0438\u0435 \u043e\u043a\u043d\u0430 \u043d\u0435 \u043e\u0441\u0442\u0430\u043d\u0430\u0432\u043b\u0438\u0432\u0430\u0435\u0442 \u0441\u0435\u0440\u0432\u0435\u0440\u043d\u044b\u0439 \u044d\u0442\u0430\u043f.',
};
const phases = {
  "export.metadata": "\u0427\u0442\u0435\u043d\u0438\u0435 \u0438\u0434\u0435\u043d\u0442\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440\u043e\u0432",
  "export.references": "\u0421\u0431\u043e\u0440\u043a\u0430 \u043b\u0451\u0433\u043a\u043e\u0433\u043e \u044d\u043a\u0441\u043f\u043e\u0440\u0442\u0430",
  "export.specs": "\u0427\u0442\u0435\u043d\u0438\u0435 \u0432\u044b\u0431\u0440\u0430\u043d\u043d\u043e\u0433\u043e \u0441\u043e\u0434\u0435\u0440\u0436\u0438\u043c\u043e\u0433\u043e",
  "export.compose": "\u0421\u0431\u043e\u0440\u043a\u0430 JSON",
  "preview.validate": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0444\u043e\u0440\u043c\u0430\u0442\u0430",
  "preview.bindings": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 UUID \u0438 \u043f\u0440\u0438\u043d\u0430\u0434\u043b\u0435\u0436\u043d\u043e\u0441\u0442\u0438",
  "preview.assignments": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0437\u0430\u0434\u0430\u043d\u0438\u0439",
  "preview.resources": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0440\u0435\u0441\u0443\u0440\u0441\u043e\u0432",
  "prepare.begin": "\u041f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043a\u0430 \u0438\u043c\u043f\u043e\u0440\u0442\u0430",
  "prepare.snapshot-start": "\u0421\u043d\u0438\u043c\u043e\u043a \u0438\u0437\u043c\u0435\u043d\u044f\u0435\u043c\u044b\u0445 \u043f\u043e\u043b\u0435\u0439",
  "prepare.snapshot-ready": "\u0421\u043d\u0438\u043c\u043e\u043a \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043b\u0435\u043d",
  "prepare.journal-saved": "\u041e\u043f\u0435\u0440\u0430\u0446\u0438\u044f \u0437\u0430\u043f\u0438\u0441\u0430\u043d\u0430 \u0432 \u0436\u0443\u0440\u043d\u0430\u043b",
  "layout-only.validate-ids": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0443\u0437\u043b\u043e\u0432 \u043a\u0430\u0440\u0442\u044b",
  "layout-only.assignments-loaded": "\u0423\u0437\u043b\u044b \u043a\u0430\u0440\u0442\u044b \u043f\u0440\u043e\u0432\u0435\u0440\u0435\u043d\u044b",
  "layout-only.receipt-ready": "\u0418\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f \u043a\u0430\u0440\u0442\u044b \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043b\u0435\u043d\u044b",
  "apply.bindings-checked": "\u0418\u0434\u0435\u043d\u0442\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440\u044b \u043f\u0440\u043e\u0432\u0435\u0440\u0435\u043d\u044b",
  "apply.courses-resolved": "\u041a\u0443\u0440\u0441\u044b \u043f\u043e\u0434\u0433\u043e\u0442\u043e\u0432\u043b\u0435\u043d\u044b",
  "apply.assignments-start": "\u041e\u0431\u0440\u0430\u0431\u043e\u0442\u043a\u0430 \u0437\u0430\u0434\u0430\u043d\u0438\u0439",
  "apply.assignments-done": "\u0417\u0430\u0434\u0430\u043d\u0438\u044f \u043e\u0431\u0440\u0430\u0431\u043e\u0442\u0430\u043d\u044b",
  "apply.readback-start": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442\u0430",
  "audit.done": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u0439 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430",
  "transaction.commit-start": "\u0424\u0438\u043a\u0441\u0430\u0446\u0438\u044f \u0434\u0430\u043d\u043d\u044b\u0445",
  "transaction.commit-done": "\u0414\u0430\u043d\u043d\u044b\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u044b",
  "map.begin": "\u041e\u0442\u043f\u0440\u0430\u0432\u043a\u0430 \u043a\u0430\u0440\u0442\u044b",
  "map.response-body-read": "\u041f\u043e\u043b\u0443\u0447\u0435\u043d \u043e\u0442\u0432\u0435\u0442 \u0441\u0435\u0440\u0432\u0435\u0440\u0430 \u043a\u0430\u0440\u0442\u044b",
  "map.receipt-validate": "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d\u043d\u043e\u0439 \u043a\u0430\u0440\u0442\u044b",
  "map.journal-complete": "\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435 \u043a\u0430\u0440\u0442\u044b \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d\u043e",

  authorize: '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0434\u043e\u0441\u0442\u0443\u043f\u0430',
  'export.tree': '\u041a\u0443\u0440\u0441\u044b \u0438 \u0438\u0434\u0435\u043d\u0442\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440\u044b',
  'export.map': '\u0427\u0442\u0435\u043d\u0438\u0435 \u043a\u0430\u0440\u0442\u044b',
  'export.assignments': '\u0427\u0442\u0435\u043d\u0438\u0435 \u0432\u044b\u0431\u0440\u0430\u043d\u043d\u044b\u0445 \u043f\u043e\u043b\u0435\u0439',
  'export.resources': '\u0420\u0435\u0441\u0443\u0440\u0441\u044b SQL',
  'export.build': '\u0421\u0431\u043e\u0440\u043a\u0430 JSON',
  assignments: '\u0417\u0430\u043f\u0438\u0441\u044c \u0437\u0430\u0434\u0430\u043d\u0438\u0439',
  'result.write': '\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435 \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442\u0430',
  completed: '\u042d\u0442\u0430\u043f \u0437\u0430\u0432\u0435\u0440\u0448\u0451\u043d',
};
function Row({ row, controller, operation }) {
  const active = isTransferActive(row);
  const stale = active && row.state === 'running' && Date.now() - Date.parse(row.heartbeatAt || row.updatedAt) > 15000;
  const pendingMap = ['import', 'resume'].includes(row.kind) && row.state === 'succeeded'
    && !(operation?.operationId === row.operationId && operation.state === 'completed');
  const percentage = transferImportPercent(row, operation);
  const seconds = Math.max(0, Math.floor((Date.parse(row.finishedAt || new Date().toISOString()) - Date.parse(row.startedAt || row.createdAt)) / 1000));
  let failure; try { failure = JSON.parse(row.errorJson || '{}'); } catch { failure = {}; }
  return <div className="rounded-lg border border-[rgba(var(--border)/0.6)] p-3 text-sm" data-testid="graph-transfer-job">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <strong>{text[row.kind] || row.kind} <span className="font-normal text-neutral-500">{text[row.lane]}</span></strong>
      <span>{row.cancelRequested && active ? text.stopping : pendingMap ? text.awaitingMap : text[row.state]}{!['failed', 'cancelled', 'expired'].includes(row.state) ? ` \u00b7 ${percentage}%` : ''}</span>
    </div>
    <progress className="mt-2 h-2 w-full accent-[rgb(var(--accent))]" value={percentage} max="100" aria-label={text[row.kind] || row.kind} />
    <div className="mt-1 flex flex-wrap justify-between gap-2 text-xs text-neutral-500">
      <span>{phases[row.phase] || row.phase}{row.totalItems > 0 ? ` \u00b7 ${row.completedItems}/${row.totalItems}` : ''}</span>
      <span>{Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}{row.bytesWritten > 0 ? ` \u00b7 ${(row.bytesWritten / 1048576).toFixed(2)} MiB gzip` : ''}</span>
    </div>
    {pendingMap ? <p className="mt-2">{text.pendingMap}</p> : null}
    {stale ? <p role="status" className="mt-2 text-amber-600 dark:text-amber-300">{text.unavailable}</p> : null}
    {failure.code || failure.message ? <p role="alert" className="mt-2 break-words">{failure.message || failure.code}</p> : null}
    {['import', 'resume', 'map'].includes(row.kind) && ['cancelled', 'failed'].includes(row.state) ? <p className="mt-1 text-xs">{text.noRollback}</p> : null}
    <div className="mt-2 flex flex-wrap gap-2">
      {active ? <Button variant="outline" disabled={controller.actionId === row.transferId || row.cancelRequested} onClick={() => {
        if (['import', 'resume', 'map'].includes(row.kind) && !window.confirm(`${text.stop}? ${text.noRollback}`)) return;
        void controller.act(row, 'cancel');
      }}>{text.stop}</Button> : null}
      {['failed', 'cancelled'].includes(row.state) && Date.parse(row.expiresAt) > Date.now() && !['preview', 'snapshot'].includes(row.kind)
        ? <Button variant="outline" disabled={controller.actionId === row.transferId} onClick={() => controller.act(row, 'retry')}>{text.retry}</Button> : null}
      {row.kind === 'export' && row.state === 'succeeded' && Date.parse(row.expiresAt) > Date.now()
        ? <Button variant="outline" disabled={controller.actionId === row.transferId} onClick={() => controller.act(row, 'download')}>{text.download}</Button> : null}
    </div>
    <details className="mt-2 text-xs text-neutral-500"><summary className="cursor-pointer">ID / {row.phase}</summary>
      <div className="break-all">{row.transferId}{row.operationId ? ` / ${row.operationId}` : ''}</div>
      {row.currentItem ? <div className="break-all">{row.currentItem}</div> : null}
      <div>{row.createdAt} / {row.heartbeatAt || row.updatedAt}</div>
    </details>
  </div>;
}
export default function GraphTransferProgress({ controller, operation, busy = false, compact = false }) {
  const active = controller.rows.filter(isTransferActive);
  const recent = controller.rows.filter((row) => !isTransferActive(row));
  const shown = active.length ? active : recent.slice(0, compact ? 1 : 2);
  const rest = controller.rows.filter((row) => !shown.includes(row));
  if (!shown.length && !busy && !controller.lookupError) return null;
  return <section className="mb-3 space-y-2 rounded-xl border p-3" aria-label={text.heading}>
    <div className="flex items-center justify-between gap-2 text-sm"><strong>{text.heading}</strong>
      <button type="button" className="text-xs underline" onClick={() => controller.refresh()}>{text.status}</button>
    </div>
    {busy && !active.length ? <div className="text-xs" role="status">{text.sending}<progress className="mt-1 h-2 w-full" aria-label={text.sending} /></div> : null}
    {controller.lookupError ? <p className="text-xs" role="status">{text.unavailable}</p> : null}
    <div className="max-h-72 space-y-2 overflow-auto">{shown.map((row) => <Row key={row.transferId} row={row} controller={controller} operation={operation} />)}</div>
    {!compact && rest.length ? <details><summary className="cursor-pointer text-xs">{text.history} ({rest.length})</summary>
      <div className="mt-2 max-h-80 space-y-2 overflow-auto">{rest.map((row) => <Row key={row.transferId} row={row} controller={controller} operation={operation} />)}</div>
    </details> : null}
    <p className="text-xs text-neutral-500">{text.closed}</p>
  </section>;
}
