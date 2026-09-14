import React, { useEffect, useState } from 'react';
import { ArrowLeft } from 'lucide-react';
import { useNavigate, useParams } from 'react-router-dom';
import { sqlAssignment } from '../api/sqlTasks';
import { getApiErrorMessage } from '../api/http';
import { Button, Card } from '../components/ui';
import SqlDatabaseViewer from '../features/sql-task/SqlDatabaseViewer';

export default function SqlDatabasePage() {
  const { assignmentId } = useParams();
  const navigate = useNavigate();
  const [spec, setSpec] = useState(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let live = true;
    (async () => {
      try {
        const data = await sqlAssignment(assignmentId);
        if (live) setSpec(data);
      } catch (e) {
        if (live) setError(getApiErrorMessage(e));
      }
    })();
    return () => { live = false; };
  }, [assignmentId]);

  return (
    <div className="max-w-[96rem] mx-auto px-4 py-6 space-y-4">
      <div className="flex items-center gap-3">
        <Button type="button" variant="ghost" className="inline-flex items-center gap-2" onClick={() => navigate(`/assignment/${assignmentId}`)}>
          <ArrowLeft size={16} /> Назад
        </Button>
        <h1 className="text-xl font-semibold">База данных</h1>
      </div>

      {error ? <Card><div className="text-sm text-red-600">{error}</div></Card> : null}
      {!spec && !error ? <Card>Загрузка…</Card> : null}
      {spec ? <SqlDatabaseViewer definition={spec.definition} seed={spec.seed} /> : null}
    </div>
  );
}
