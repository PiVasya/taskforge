import React from 'react';
import { previewExportOptions, withPreviewMetadata } from './graphTransferModel';
import { getCourseMap } from '../../api/courseMaps';
import { captureTaskGraphPreviewSnapshot } from './taskGraphPreviewSnapshot';
import { exportAssignmentsToJson, previewAssignmentsFromJson } from '../../api/assignments';
import { handleApiError } from '../../utils/handleApiError';
import { buildTaskGraphImportDiff, DEFAULT_JSON_IMPORT_OPTIONS } from './courseTaskGraphJson';
import { mapRecoveryPayload, mapRequestFromOperation } from './courseTaskGraphOperation';

export default function useTaskGraphImportActions({ courseId, importContextRef, importJournal, jsonImportDiff, jsonImportOptions,
  lastImportDiffRef, jsonPreviewMapVersionRef, reloadAssignments, reloadCourseData, notify, setContentLayout, setGraphImportRequest,
  setJsonImportBusy, setJsonImportApplyError, setJsonImportDiffOpen, setJsonImportDialogOpen, setJsonImportDiff,
  setJsonImportParsed, setJsonMapRecovery, setJsonImportOptions, setJsonImportCurrentExport, setImportReloadKey }) {
  const installImportedGraph = (operation, extra = {}) => {
    if (String(operation?.courseId) !== String(courseId)) return;
    const request = mapRequestFromOperation(operation, { expectedMapVersion: jsonPreviewMapVersionRef.current, ...extra });
    if (request) { setContentLayout('flow'); setGraphImportRequest(request); }
  };

  const applyJsonImport = async (parsed) => {
    const context = importContextRef.current;
    setJsonImportBusy(true);
    setJsonImportApplyError(null);
    lastImportDiffRef.current = null;
    try {
      const operation = await importJournal.begin(parsed, { ...jsonImportOptions, expectedMapVersion: jsonPreviewMapVersionRef.current });
      if (context !== importContextRef.current) return;
      lastImportDiffRef.current = { operationId: operation.operationId, diff: jsonImportDiff };
      setJsonImportDiffOpen(false); setJsonImportDialogOpen(false);
      setJsonImportDiff(null); setJsonImportParsed(null);
      if (operation.state === 'completed') {
        await Promise.allSettled([reloadAssignments(), reloadCourseData()]);
        notify.success('Импорт завершён.');
      } else installImportedGraph(operation);
    } catch (error) {
      if (context === importContextRef.current) setJsonImportApplyError(handleApiError(error, false,
        'Результат импорта нужно проверить в журнале. Не создавайте новый импорт.'));
    } finally { if (context === importContextRef.current) setJsonImportBusy(false); }
  };

  const { getSnapshot: getImportSnapshot, remember: rememberImport, refresh: refreshImport } = importJournal;
  const handleGraphImportComplete = React.useCallback((key, result) => {
    const operation = getImportSnapshot().operation;
    if (operation?.operationId !== key) return;
    if (result?.success) {
      setGraphImportRequest((current) => current?.key === key ? null : current);
      rememberImport({ ...operation, state: 'completed' });
      void refreshImport(key).catch(() => {});
      void Promise.allSettled([reloadAssignments(), reloadCourseData()]);
    } else {
      setGraphImportRequest((current) => current?.key === key ? { ...current, status: 'failed', error: result?.message } : current);
      void refreshImport(key).catch(() => {});
    }
  }, [getImportSnapshot, rememberImport, refreshImport, reloadAssignments, reloadCourseData, setGraphImportRequest]);

  const prepareMapRecovery = async (operation = importJournal.operation) => {
    const context = importContextRef.current;
    setJsonImportBusy(true);
    try {
      const parsed = mapRecoveryPayload(operation);
      const options = { ...DEFAULT_JSON_IMPORT_OPTIONS, updateContent: false, updateChecks: false, updateVisibility: false,
        connectionMode: operation.response.taskGraph.apply?.connectionMode || 'replace',
        updateConnections: Boolean(operation.response.taskGraph.apply?.connections),
        updateConnectionAccess: Boolean(operation.response.taskGraph.apply?.connectionAccess),
        updateLayout: Boolean(operation.response.taskGraph.apply?.layout), allowDeletes: false,
        allowConnectionDeletes: false, allowDuplicates: false };
      const [snapshot, preflight] = await Promise.all([
        captureTaskGraphPreviewSnapshot({ readMap: () => getCourseMap(courseId), readExport: () => exportAssignmentsToJson(courseId, previewExportOptions(parsed, options)) }),
        previewAssignmentsFromJson(courseId, parsed, options),
      ]);
      if (String(operation.courseId) !== String(courseId) || context !== importContextRef.current) return;
      const current = withPreviewMetadata(snapshot.currentExport, preflight);
      jsonPreviewMapVersionRef.current = snapshot.mapVersion;
      setJsonMapRecovery(true); setJsonImportOptions(options); setJsonImportParsed(parsed);
      setJsonImportCurrentExport(current); setJsonImportDiff(buildTaskGraphImportDiff(parsed, current, options, preflight));
      setJsonImportApplyError(null); setJsonImportDiffOpen(true);
    } catch (error) { handleApiError(error, notify, 'Не удалось пересчитать карту.'); }
    finally { if (context === importContextRef.current) setJsonImportBusy(false); }
  };

  const resumeImport = async () => {
    const context = importContextRef.current;
    try {
      const operation = await importJournal.resume();
      if (!operation || String(operation.courseId) !== String(courseId) || context !== importContextRef.current) return;
      if (operation.state === 'completed') {
        setGraphImportRequest(null); setImportReloadKey((n) => n + 1);
        await Promise.allSettled([reloadAssignments(), reloadCourseData()]);
        notify.success('Импорт завершён. Карта сохранена.');
      } else if (operation.response && !operation.mapRequest) await prepareMapRecovery(operation);
    } catch (error) { handleApiError(error, notify, 'Импорт ещё не завершён.'); }
  };

  return { installImportedGraph, applyJsonImport, handleGraphImportComplete, prepareMapRecovery, resumeImport };
}
