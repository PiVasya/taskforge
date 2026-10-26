import { exportAssignmentsToBlob } from '../../api/assignments';
import { saveGraphTransferBlob } from '../../api/graphTransfers';
import { taskGraphExportIssue } from './courseTaskGraphJson';
import { handleApiError } from '../../utils/handleApiError';

export default function useTaskGraphExport({ courseId, jsonExportOptions, importContextRef, ensureCanManageAssignments,
  notify, setJsonExportBusy, setJsonExportDialogOpen }) {
  return async () => {
    if (!ensureCanManageAssignments("экспортировать JSON")) return;
    const issue = taskGraphExportIssue(jsonExportOptions);
    if (issue) { notify.warn(issue); return; }
    const context = importContextRef.current;
    setJsonExportBusy(true);
    try {
      const blob = await exportAssignmentsToBlob(courseId, jsonExportOptions);
      if (context !== importContextRef.current) return;
      saveGraphTransferBlob(blob, `taskforge-${courseId}-task-graph.json`);
      setJsonExportDialogOpen(false);
      notify.success("Файл экспорта готов");
    } catch (e) {
      if (context !== importContextRef.current) return;
      handleApiError(e, notify, "Не удалось экспортировать JSON");
    } finally {
      if (context === importContextRef.current) setJsonExportBusy(false);
    }
  };
}
