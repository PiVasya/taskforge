import React, { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Check, CheckCircle2, Database, Play, RotateCcw, XCircle } from 'lucide-react';
import { useAuth } from '../../auth/AuthContext';
import CodeEditor from '../../components/CodeEditor';
import { Button, Card, Select } from '../../components/ui';
import { getApiErrorMessage } from '../../api/http';
import { getMySolutionDetails } from '../../api/solutions';
import { checkSql, runSql, sqlAssignment, sqlPreview } from '../../api/sqlTasks';
import { isPending, ownSnapshot, resultLabel } from './sqlModel';
import { SolveActionDock } from '../assignment-solve/components/AssignmentSolvePresentation';
import SqlSnapshot from './SqlSnapshot';
import './sql-task.css';

const read = key => { try { return JSON.parse(localStorage.getItem(key) || 'null'); } catch { return null; } };
const write = (key, value) => { try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, JSON.stringify(value)); } catch {} };

export default function SqlTaskSolve({
  assignment,
  statement,
  layout = 'split',
  nextOptions = [],
  nextLoading = false,
  nextDisabled = false,
  onNext,
  onActivity,
  onCompleted,
}) {
  const navigate = useNavigate();
  const { user } = useAuth();
  const owner = user?.id || user?.userId || 'session';
  const key = `taskforge-sql:${owner}:${assignment.id}`;
  const [spec, setSpec] = useState(null);
  const [engineId, setEngineId] = useState('');
  const [source, setSource] = useState('');
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState('');
  const [receipt, setReceipt] = useState(null);
  const [retry, setRetry] = useState(null);
  const [result, setResult] = useState(null);
  const [status, setStatus] = useState('');
  const mounted = useRef(true);
  const completed = useRef(new Set());
  const completedCallback = useRef(onCompleted);
  const activityCallback = useRef(onActivity);
  completedCallback.current = onCompleted;
  activityCallback.current = onActivity;

  const currentDraftKey = spec && engineId ? `${key}:${spec.specVersionId}:${engineId}` : '';

  useEffect(() => {
    let live = true;
    mounted.current = true;
    (async () => {
      try {
        const data = await sqlAssignment(assignment.id);
        if (!live) return;
        setSpec(data);
        const savedEngine = read(`${key}:engine`);
        const target = data.targets.find(x => x.engineProfileId === savedEngine) || data.targets[0];
        setEngineId(target?.engineProfileId || '');
        const draft = target ? read(`${key}:${data.specVersionId}:${target.engineProfileId}`) : null;
        setSource(typeof draft === 'string' ? draft : target?.starterSql || '');
        setReceipt(read(`${key}:receipt`));
        setRetry(read(`${key}:request`));
      } catch (e) {
        if (live) setError(getApiErrorMessage(e));
      } finally {
        if (live) setLoading(false);
      }
    })();
    return () => {
      live = false;
      mounted.current = false;
    };
  }, [assignment.id, key]);

  useEffect(() => {
    if (currentDraftKey && !loading) write(currentDraftKey, source);
  }, [currentDraftKey, source, loading]);

  const finish = (kind, data) => {
    setStatus(data.status || data.verdict);
    setResult(ownSnapshot(data.result));
    setError('');
    if (kind === 'check') {
      window.dispatchEvent(new Event('quota:changed'));
      if (!completed.current.has(data.id)) {
        completed.current.add(data.id);
        completedCallback.current?.();
      }
    }
    activityCallback.current?.(
      kind === 'check' ? 'submit_finished' : 'sql_preview_finished',
      { language: 'sql', payload: { status: data.status || data.verdict, engineProfileId: data.sqlEngineProfileId || engineId } },
    );
  };

  useEffect(() => {
    if (!receipt?.id) return undefined;
    let stopped = false;
    let timer;
    const poll = async () => {
      try {
        const data = receipt.kind === 'check' ? await getMySolutionDetails(receipt.id) : await sqlPreview(receipt.id);
        if (stopped) return;
        setStatus(data.status || data.verdict);
        if (!isPending(data.status || data.verdict)) {
          finish(receipt.kind, data);
          setReceipt(null);
          write(`${key}:receipt`, null);
          return;
        }
      } catch (e) {
        if (stopped) return;
        setError(getApiErrorMessage(e));
        if ([403, 404, 410].includes(e?.response?.status)) {
          setReceipt(null);
          write(`${key}:receipt`, null);
          return;
        }
      }
      if (!stopped) timer = setTimeout(poll, 800);
    };
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [receipt?.id, key]);

  const submit = async (kind, savedRequest = null) => {
    if (sending || receipt) return;
    const request = savedRequest || {
      kind,
      input: {
        assignmentId: assignment.id,
        engineProfileId: engineId,
        sql: source,
        requestId: crypto.randomUUID(),
      },
    };
    if (!request.input.sql.trim()) return;
    setSending(true);
    setError('');
    setStatus('queued');
    setRetry(request);
    write(`${key}:request`, request);
    try {
      const data = request.kind === 'check' ? await checkSql(request.input) : await runSql(request.input);
      if (!mounted.current) return;
      setRetry(null);
      write(`${key}:request`, null);
      activityCallback.current?.(
        request.kind === 'check' ? 'submit_started' : 'sql_preview_started',
        {
          language: 'sql',
          codeLength: request.input.sql.length,
          fullCode: request.input.sql,
          payload: { engineProfileId: request.input.engineProfileId },
        },
      );
      if (request.kind === 'check' && !isPending(data.status || data.verdict)) {
        finish('check', data);
      } else {
        const active = {
          id: data.jobId || data.id,
          kind: request.kind,
          engineProfileId: request.input.engineProfileId,
        };
        write(`${key}:receipt`, active);
        setReceipt(active);
        setStatus(data.status || 'queued');
      }
    } catch (e) {
      if (!mounted.current) return;
      setError(getApiErrorMessage(e));
      setStatus('');
      if (e?.response?.status && e.response.status < 500) {
        setRetry(null);
        write(`${key}:request`, null);
      }
    } finally {
      if (mounted.current) setSending(false);
    }
  };

  const selectEngine = id => {
    write(currentDraftKey, source);
    setEngineId(id);
    write(`${key}:engine`, id);
    const draft = read(`${key}:${spec.specVersionId}:${id}`);
    const target = spec.targets.find(item => item.engineProfileId === id);
    setSource(typeof draft === 'string' ? draft : target?.starterSql || '');
    setResult(null);
    setStatus('');
    setError('');
  };

  if (loading) {
    return <Card className="sql-loading-card" role="status">Загрузка…</Card>;
  }
  if (!spec) {
    return <Card className="sql-loading-card"><div className="sql-inline-error" role="alert">{error || 'Задание ещё не опубликовано.'}</div></Card>;
  }

  const target = spec.targets.find(item => item.engineProfileId === engineId);
  const busy = sending || !!receipt;
  const disabled = busy || !!retry || !source.trim() || !engineId;
  const successful = status === 'Accepted';
  const showFeedback = Boolean(!busy && status && status !== 'Previewed');
  const statusText = busy ? (String(status).toLowerCase() === 'running' ? 'Выполняется…' : 'В очереди…') : '';

  const editorCard = (
    <Card className="sql-editor-card">
      <div className="sql-editor-header">
        <div className="sql-editor-title">Написать SQL</div>
        <div className="sql-editor-tools">
          {spec.targets.length > 1 ? (
            <Select
              className="sql-engine-select"
              aria-label="SQL-движок"
              value={engineId}
              disabled={busy || !!retry}
              onChange={event => selectEngine(event.target.value)}
            >
              {spec.targets.map(item => (
                <option value={item.engineProfileId} key={item.engineProfileId}>{item.displayName}</option>
              ))}
            </Select>
          ) : null}
          <Button
            type="button"
            variant="ghost"
            className="sql-db-button"
            onClick={() => navigate(`/assignment/${assignment.id}/database`)}
          >
            <Database size={16} />
            <span>База данных</span>
          </Button>
          <Button
            type="button"
            variant="ghost"
            className="sql-reset-button"
            aria-label="Сбросить SQL"
            title="Сбросить SQL"
            disabled={busy || !!retry}
            onClick={() => {
              if (window.confirm('Восстановить стартовый SQL?')) setSource(target?.starterSql || '');
            }}
          >
            <RotateCcw size={16} />
          </Button>
        </div>
      </div>

      <div className="sql-editor-wrap">
        <CodeEditor
          language="sql"
          modelPath={`sql-solve-${owner}-${assignment.id}-${spec.specVersionId}-${engineId}`}
          value={source}
          onChange={value => setSource(value || '')}
          readOnly={sending || !!retry}
          height={layout === 'editorTop' ? 460 : 380}
          automationId={`sql-source-${assignment.id}`}
        />
      </div>

      {retry && !busy ? (
        <div className="sql-retry" role="alert">
          <span>Ответ не получен.</span>
          <Button type="button" variant="outline" onClick={() => void submit(retry.kind, retry)}>Повторить</Button>
        </div>
      ) : null}
      {error ? <div role="alert" className="sql-inline-error">{error}</div> : null}
      {showFeedback ? (
        <div className={successful ? 'sql-feedback is-success' : 'sql-feedback is-error'} role="status">
          {successful ? <CheckCircle2 size={17} /> : <XCircle size={17} />}
          <span>{resultLabel(status)}</span>
          {result?.error ? <span className="sql-feedback-detail">{result.error.message}</span> : null}
        </div>
      ) : null}
    </Card>
  );

  const resultCard = result ? <SqlSnapshot snapshot={result} /> : null;

  return (
    <div className="sql-task-solve">
      {layout === 'editorTop' ? (
        <div className="space-y-6">
          {editorCard}
          {statement}
          {resultCard}
        </div>
      ) : (
        <div className="grid lg:grid-cols-3 gap-6">
          <div className="lg:col-span-2 space-y-5">
            {statement}
            {resultCard}
          </div>
          <div className="space-y-4">
            {editorCard}
          </div>
        </div>
      )}

      <SolveActionDock
        nextOptions={nextOptions}
        nextLoading={nextLoading}
        nextDisabled={nextDisabled}
        onNext={onNext}
        statusText={statusText}
        primaryLabel="Проверить"
        primaryIcon={Check}
        primaryDisabled={disabled}
        primaryAutomationId="submit-sql-solution"
        primaryAgentAction="submit-sql-solution"
        onPrimary={() => void submit('check')}
        secondaryActions={[{
          key: 'sql-run',
          label: 'Запустить',
          icon: <Play size={16} />,
          variant: 'outline',
          disabled,
          onClick: () => void submit('run'),
        }]}
      />
    </div>
  );
}
