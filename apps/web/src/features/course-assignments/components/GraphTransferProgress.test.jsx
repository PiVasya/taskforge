import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import GraphTransferProgress from './GraphTransferProgress';
import TaskGraphImportStatus, { TaskGraphImportHistory } from './TaskGraphImportStatus';
import { transferImportPercent } from '../graphTransferModel';

const finishedImport = {
  transferId: 'transfer-import', operationId: 'operation-import', kind: 'import', lane: 'content',
  state: 'succeeded', percent: 100, phase: 'completed', completedItems: 792, totalItems: 792,
  createdAt: '2026-11-01T14:00:00Z', startedAt: '2026-11-01T14:00:00Z',
  finishedAt: '2026-11-01T14:00:06Z',
};
const finishedMap = {
  ...finishedImport, transferId: 'transfer-map', kind: 'map', lane: 'metadata',
  createdAt: '2026-11-01T14:00:07Z', startedAt: '2026-11-01T14:00:07Z',
};
function controller(rows) {
  return { rows, lookupError: false, actionId: null, act: jest.fn(), refresh: jest.fn() };
}

test('completed transfers without history are not rendered in the dialogs', () => {
  const html = renderToStaticMarkup(
    <GraphTransferProgress controller={controller([finishedMap, finishedImport])}
      operation={{ operationId: 'operation-import', state: 'completed' }} showHistory={false} />,
  );
  expect(html).toBe('');
});

test('running work is visible only in a compact progress line inside the dialog', () => {
  const live = { ...finishedImport, state: 'running', percent: 50, completedItems: 396,
    phase: 'apply.assignments-start', heartbeatAt: new Date().toISOString() };
  const html = renderToStaticMarkup(
    <GraphTransferProgress controller={controller([finishedMap, live])} operation={null} showHistory={false} />,
  );
  expect(html).toContain('graph-transfer-dialog-status');
  expect(html).toContain('396/792');
  expect(html).toContain('35%');
  expect(html).not.toContain('graph-transfer-page-status');
  expect((html.match(/<progress/g) || [])).toHaveLength(1);
});

test('completed jobs in the import dialog appear only inside a closed history disclosure', () => {
  const html = renderToStaticMarkup(
    <GraphTransferProgress controller={controller([finishedMap, finishedImport])} operation={null} showHistory />,
  );
  expect(html).toContain('История операций');
  expect(html).toContain('<details');
  expect(html).not.toContain('<details open=');
  expect(html).not.toContain('graph-transfer-active-job');
});

test('a blocked import keeps an explicit compact recovery action', () => {
  const op = { operationId: 'op1', courseId: 'c1', state: 'map-conflict', response: { taskGraph: {} } };
  const journal = { operation: op, ready: true, lookupError: null, busy: false, history: [op], refresh: jest.fn() };
  const html = renderToStaticMarkup(<TaskGraphImportStatus importJournal={journal}
    graphImportRequest={{ status: 'failed' }} jsonImportBusy={false}
    prepareMapRecovery={jest.fn()} resumeImport={jest.fn()}
    notify={{}} lastImportDiffRef={{ current: null }} />);
  expect(html).toContain('Пересчитать карту');
  expect(html).toContain('import-operation-status');
  expect(html).not.toContain('История импортов (1)');
  expect(renderToStaticMarkup(<TaskGraphImportStatus importJournal={{ ...journal, operation: { ...op, state: 'completed' } }} />)).toBe('');
});

test('import completion reaches 100% only after the matching map receipt', () => {
  expect(transferImportPercent(finishedImport, null)).toBe(70);
  expect(transferImportPercent(finishedMap, null)).toBe(99);
  expect(transferImportPercent(finishedImport, { operationId: 'operation-import', state: 'data-committed' })).toBe(70);
  expect(transferImportPercent(finishedImport, { operationId: 'operation-import', state: 'completed' })).toBe(100);
  expect(transferImportPercent(finishedImport, { operationId: 'other-operation', state: 'completed' })).toBe(70);
  expect(transferImportPercent({ kind: 'export', state: 'succeeded', percent: 100 }, null)).toBe(100);
  expect(transferImportPercent({ kind: 'import', state: 'running', percent: 50 }, null)).toBe(35);
  expect(transferImportPercent({ kind: 'map', state: 'running', percent: 50 }, null)).toBe(84);
});

test('successful import reports remain accessible inside the modal history', () => {
  const journal = { history: [{ operationId: 'operation-import', state: 'completed', createdAt: '2026-11-01T14:00:00Z' }] };
  const historyContent = <TaskGraphImportHistory importJournal={journal} courseId="course-1"
    notify={{}} lastImportDiffRef={{ current: null }} />;
  const html = renderToStaticMarkup(<GraphTransferProgress controller={controller([finishedMap])}
    showHistory historyContent={historyContent} />);
  expect(html).toContain('История операций');
  expect(html).toContain('Импорты');
  expect(html).toContain('Скачать отчёт');
  expect(html).toContain('<details');
  expect(html).not.toContain('<details open=');
});
