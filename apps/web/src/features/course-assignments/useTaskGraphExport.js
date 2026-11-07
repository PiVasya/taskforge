import { exportAssignmentsToBlob } from '../../api/assignments';
import { saveGraphTransferBlob } from '../../api/graphTransfers';
import { taskGraphExportIssue } from './courseTaskGraphJson';
import { handleApiError } from '../../utils/handleApiError';
import { exportGraphImagePackage } from './images/courseImageTransfer';

export default function useTaskGraphExport({ courseId, jsonExportOptions, jsonExportFormat, jsonExportGallery, importContextRef, ensureCanManageAssignments,
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
      if (jsonExportFormat === 'zip') {
        const packageResult = await exportGraphImagePackage(blob, courseId, { includeGallery: jsonExportGallery });
        if (context !== importContextRef.current) return;
        saveGraphTransferBlob(packageResult.blob, `taskforge-${courseId}-task-graph.zip`);
        notify.success(`ZIP экспортирован: изображений ${packageResult.imageCount}`);
        if (packageResult.externalImages) notify.warn(`Внешних изображений без локальных файлов: ${packageResult.externalImages}. Их ссылки оставлены в JSON.`);
      } else {
        saveGraphTransferBlob(blob, `taskforge-${courseId}-task-graph.json`);
        notify.success('JSON экспортирован');
      }
      setJsonExportDialogOpen(false);
    } catch (e) {
      if (context !== importContextRef.current) return;
      handleApiError(e, notify, "Не удалось экспортировать JSON");
    } finally {
      if (context === importContextRef.current) setJsonExportBusy(false);
    }
  };
}
