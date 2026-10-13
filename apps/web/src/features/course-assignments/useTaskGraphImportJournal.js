import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { getTaskGraphImport, importAssignmentsFromJson, listTaskGraphImports, resumeTaskGraphImport, stopTaskGraphImport } from '../../api/assignments';
import { createTaskGraphJournalClient } from './taskGraphJournalClient';

const api = { get: getTaskGraphImport, begin: importAssignmentsFromJson, list: listTaskGraphImports,
  resume: resumeTaskGraphImport, stop: stopTaskGraphImport };

export default function useTaskGraphImportJournal({ courseId, userId, enabled }) {
  const client = useMemo(() => createTaskGraphJournalClient({ courseId, userId, enabled, api }), [courseId, userId, enabled]);
  const state = useSyncExternalStore(client.subscribe, client.getSnapshot, client.getSnapshot);
  useEffect(() => { void client.discover(); }, [client]);
  return { ...state, ...client };
}
