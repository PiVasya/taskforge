import { useState } from 'react';
import { deleteUserSolutions, deleteUserImageSolutions } from '../../api/admin';
import { deleteUserTaskTestAttempts } from '../../api/taskTestAttempts';
import { deleteUserMathAttempts } from '../../api/mathTaskAttempts';
import { useQueryClient } from '../../data/QueryClientProvider';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { bulkDeleteTarget, bulkDeleteMessage } from './adminSolutionBulkDeleteModel';

export default function useAdminSolutionsBulkDelete({ userId, tab, users, onSuccess, onError }) {
  const queryClient = useQueryClient();
  const notify = useNotify();
  const [bulkDeletingTab, setBulkDeletingTab] = useState(null);

  const handleDeleteAll = async () => {
    const target = bulkDeleteTarget(userId, tab);
    const user = users.find((item) => item.id === target?.userId);
    if (!target || !user || bulkDeletingTab) return;
    if (!window.confirm(bulkDeleteMessage(target, user))) return;

    setBulkDeletingTab(target.tab);
    try {
      const actions = {
        code: () => deleteUserSolutions(target.userId, { kind: 'code' }),
        sql: () => deleteUserSolutions(target.userId, { kind: 'sql' }),
        tests: () => deleteUserTaskTestAttempts(target.userId),
        images: () => deleteUserImageSolutions(target.userId),
        math: () => deleteUserMathAttempts(target.userId),
      };
      const result = await actions[target.tab]();
      onSuccess(target);
      await queryClient.invalidateQueries({ queryKey: ['admin-solutions', target.query, target.userId] });
      await queryClient.invalidateQueries({ queryKey: ['admin-solutions', 'users'], refetch: false });
      notify.success(`Удалено ${Number(result?.deleted ?? 0)} записей`);
    } catch (e) {
      onError(handleApiError(e, notify, 'Не удалось удалить решения'));
    } finally {
      setBulkDeletingTab(null);
    }
  };

  return { bulkDeletingTab, handleDeleteAll };
}
