import React, { useEffect, useRef, useState } from 'react';
import { useAuth } from '../../auth/AuthContext';
import CodeEditor from '../../components/CodeEditor';
import { getApiErrorMessage } from '../../api/http';
import { getMySolutionDetails, listMySolutions } from '../../api/solutions';
import { checkSql, runSql, sqlAssignment, sqlPreview, sqlRuntime } from '../../api/sqlTasks';
import { isPending, ownSnapshot, resultLabel } from './sqlModel';
import SqlSnapshot from './SqlSnapshot';
import { F } from './SqlDatasetEditor';
import './sql-task.css';

const read = key => { try { return JSON.parse(localStorage.getItem(key) || 'null'); } catch { return null; } };
const write = (key,value) => { try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key,JSON.stringify(value)); } catch {} };
export default function SqlTaskSolve({ assignment, onActivity, onCompleted }) {
  const { user } = useAuth();
  const owner = user?.id || user?.userId || 'session';
  const key = `taskforge-sql:${owner}:${assignment.id}`;
  const [spec, setSpec] = useState(null), [engineId, setEngineId] = useState(''), [source, setSource] = useState('');
  const [runtime, setRuntime] = useState(null), [loading, setLoading] = useState(true), [sending, setSending] = useState(false);
  const [error, setError] = useState(''), [receipt, setReceipt] = useState(null), [retry, setRetry] = useState(null);
  const [result, setResult] = useState(null), [status, setStatus] = useState(''), [history, setHistory] = useState([]);
  const mounted = useRef(true), completed = useRef(new Set()), completedCallback = useRef(onCompleted), activityCallback = useRef(onActivity);
  completedCallback.current = onCompleted; activityCallback.current = onActivity;
  const currentDraftKey = spec && engineId ? `${key}:${spec.specVersionId}:${engineId}` : '';
  const loadHistory = async () => { try { const rows = await listMySolutions(assignment.id); if (mounted.current) setHistory((Array.isArray(rows) ? rows : rows?.items || []).filter(s => s.kind === 'sql' || s.sqlSpecVersionId).slice(0,20)); } catch {} };
  useEffect(() => {
    let live = true; mounted.current = true;
    (async () => {
      try {
        const data = await sqlAssignment(assignment.id); if (!live) return;
        setSpec(data); const savedEngine = read(`${key}:engine`);
        const target = data.targets.find(x => x.engineProfileId === savedEngine) || data.targets[0];
        setEngineId(target?.engineProfileId || '');
        const draft = target ? read(`${key}:${data.specVersionId}:${target.engineProfileId}`) : null;
        setSource(typeof draft === 'string' ? draft : target?.starterSql || '');
        setReceipt(read(`${key}:receipt`)); setRetry(read(`${key}:request`));
        await loadHistory();
      } catch(e) { if (live) setError(getApiErrorMessage(e)); }
      finally { if (live) setLoading(false); }
    })();
    return () => { live = false; mounted.current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assignment.id, key]);
  useEffect(() => { if (currentDraftKey && !loading) write(currentDraftKey,source); }, [currentDraftKey,source,loading]);
  useEffect(() => {
    let live = true;
    const refresh = async () => { try { const data = await sqlRuntime(); if (live) setRuntime(data); } catch { if (live) setRuntime(null); } };
    void refresh(); const timer = setInterval(refresh,5000); return () => { live = false; clearInterval(timer); };
  }, []);
  const finish = (kind, data) => {
    setStatus(data.status || data.verdict); setResult(ownSnapshot(data.result)); setError('');
    if (kind === 'check') {
      void loadHistory(); window.dispatchEvent(new Event('quota:changed'));
      if (!completed.current.has(data.id)) { completed.current.add(data.id); completedCallback.current?.(); }
    }
    activityCallback.current?.(kind === 'check' ? 'submit_finished' : 'sql_preview_finished',{language:'sql',payload:{status:data.status || data.verdict,engineProfileId:data.sqlEngineProfileId || engineId}});
  };
  useEffect(() => {
    if (!receipt?.id) return undefined;
    let stopped = false, timer;
    const poll = async () => {
      try {
        const data = receipt.kind === 'check' ? await getMySolutionDetails(receipt.id) : await sqlPreview(receipt.id);
        if (stopped) return;
        setStatus(data.status || data.verdict);
        if (!isPending(data.status || data.verdict)) {
          finish(receipt.kind,data); setReceipt(null); write(`${key}:receipt`,null); return;
        }
      } catch(e) {
        if (stopped) return;
        setError(getApiErrorMessage(e));
        if ([403,404,410].includes(e?.response?.status)) { setReceipt(null); write(`${key}:receipt`,null); return; }
      }
      if (!stopped) timer = setTimeout(poll,800);
    };
    void poll(); return () => { stopped = true; clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [receipt?.id,key]);
  const submit = async (kind, savedRequest = null) => {
    if (sending || receipt) return;
    const request = savedRequest || { kind, input:{ assignmentId:assignment.id,engineProfileId:engineId,sql:source,requestId:crypto.randomUUID() } };
    if (!request.input.sql.trim()) return;
    setSending(true); setError(''); setStatus('queued'); setRetry(request); write(`${key}:request`,request);
    try {
      const data = request.kind === 'check' ? await checkSql(request.input) : await runSql(request.input);
      if (!mounted.current) return;
      setRetry(null); write(`${key}:request`,null);
      activityCallback.current?.(request.kind === 'check' ? 'submit_started' : 'sql_preview_started',{language:'sql',codeLength:request.input.sql.length,fullCode:request.input.sql,payload:{engineProfileId:request.input.engineProfileId}});
      if (request.kind === 'check' && !isPending(data.status || data.verdict)) finish('check',data);
      else {
        const active = {id:data.jobId || data.id,kind:request.kind,engineProfileId:request.input.engineProfileId};
        write(`${key}:receipt`,active); setReceipt(active); setStatus(data.status || 'queued');
      }
    } catch(e) {
      if (!mounted.current) return;
      setError(getApiErrorMessage(e)); setStatus('');
      if (e?.response?.status && e.response.status < 500) { setRetry(null); write(`${key}:request`,null); }
    } finally { if (mounted.current) setSending(false); }
  };
  const selectEngine = id => {
    write(currentDraftKey,source); setEngineId(id); write(`${key}:engine`,id);
    const draft = read(`${key}:${spec.specVersionId}:${id}`); const target = spec.targets.find(t => t.engineProfileId === id);
    setSource(typeof draft === 'string' ? draft : target?.starterSql || ''); setResult(null); setStatus('');
  };
  if (loading) return <div className="sql-task"><div className="sql-card" role="status">{'\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 SQL-\u0437\u0430\u0434\u0430\u043d\u0438\u044f\u2026'}</div></div>;
  if (!spec) return <div className="sql-task"><div className="sql-error" role="alert">{error || '\u0417\u0430\u0434\u0430\u043d\u0438\u0435 \u0435\u0449\u0451 \u043d\u0435 \u043e\u043f\u0443\u0431\u043b\u0438\u043a\u043e\u0432\u0430\u043d\u043e.'}</div></div>;
  const target = spec.targets.find(t => t.engineProfileId === engineId);
  const online = (runtime?.workers || []).some(w => w.targets?.includes(target?.fingerprint));
  const busy = sending || !!receipt;
  return <div className="sql-task">
    <div className="sql-card"><div className="sql-toolbar"><F label={'\u0414\u0432\u0438\u0436\u043e\u043a'}><select value={engineId} disabled={busy || !!retry} onChange={e => selectEngine(e.target.value)}>{spec.targets.map(t => <option value={t.engineProfileId} key={t.engineProfileId}>{t.displayName}</option>)}</select></F><span className="sql-badge">{spec.mode}</span><span className="sql-badge">{online ? 'ONLINE' : 'OFFLINE'}</span><span className="sql-muted">{spec.limits.timeoutMs} ms / {spec.limits.maxRows} rows</span></div>
      <p className="sql-muted">{'\u041a\u0430\u0436\u0434\u044b\u0439 Run \u0438 Check \u043d\u0430\u0447\u0438\u043d\u0430\u0435\u0442\u0441\u044f \u0441 \u043d\u043e\u0432\u043e\u0439 \u043a\u043e\u043f\u0438\u0438 \u0438\u0441\u0445\u043e\u0434\u043d\u043e\u0439 \u0411\u0414. \u041c\u0435\u0436\u0434\u0443 \u0437\u0430\u043f\u0443\u0441\u043a\u0430\u043c\u0438 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f \u0411\u0414 \u043d\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u044f\u044e\u0442\u0441\u044f. Run \u043d\u0435 \u0442\u0440\u0430\u0442\u0438\u0442 \u044d\u043d\u0435\u0440\u0433\u0438\u044e \u0438 \u043d\u0435 \u0434\u0430\u0451\u0442 \u0431\u0430\u043b\u043b\u044b.'}</p>
      {!spec.allowMultipleStatements && <p className="sql-muted">{'\u0412 \u044d\u0442\u043e\u043c \u0437\u0430\u0434\u0430\u043d\u0438\u0438 \u0440\u0430\u0437\u0440\u0435\u0448\u0451\u043d \u043e\u0434\u0438\u043d statement.'}</p>}
      <CodeEditor language="sql" modelPath={`sql-solve-${owner}-${assignment.id}-${spec.specVersionId}-${engineId}`} value={source} onChange={v => setSource(v || '')} readOnly={sending || !!retry} height={360} automationId={`sql-source-${assignment.id}`} />
      <div className="sql-toolbar" style={{ marginTop: '.8rem' }}><button type="button" disabled={busy || !!retry || !source.trim() || !engineId} onClick={() => void submit('run')}>{'Run \u2014 \u0437\u0430\u043f\u0443\u0441\u0442\u0438\u0442\u044c'}</button><button type="button" className="sql-primary" disabled={busy || !!retry || !source.trim() || !engineId} onClick={() => void submit('check')}>{'Check \u2014 \u043f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c'}</button><button type="button" disabled={busy || !!retry} onClick={() => { if (window.confirm('\u0412\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0442\u0430\u0440\u0442\u043e\u0432\u044b\u0439 SQL?')) setSource(target?.starterSql || ''); }}>{'\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c SQL'}</button>
        {busy && <span role="status">{status === 'running' || status === 'Running' ? '\u0412\u044b\u043f\u043e\u043b\u043d\u044f\u0435\u0442\u0441\u044f\u2026' : '\u0412 \u043e\u0447\u0435\u0440\u0435\u0434\u0438\u2026'}</span>}</div>
      {retry && !busy && <div className="sql-error"><p>{'\u041e\u0442\u0432\u0435\u0442 \u043d\u0430 \u0437\u0430\u043f\u0440\u043e\u0441 \u043d\u0435 \u043f\u043e\u043b\u0443\u0447\u0435\u043d. \u041f\u043e\u0432\u0442\u043e\u0440 \u0441 \u0442\u0435\u043c \u0436\u0435 ID \u043d\u0435 \u0441\u043f\u0438\u0448\u0435\u0442 \u044d\u043d\u0435\u0440\u0433\u0438\u044e \u0434\u0432\u0430\u0436\u0434\u044b.'}</p><button type="button" onClick={() => void submit(retry.kind,retry)}>{'\u041f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u044c \u0437\u0430\u043f\u0440\u043e\u0441'}</button></div>}
      {error && <div role="alert" className="sql-error">{error}</div>}
      {!busy && status && <div className={status === 'Accepted' || status === 'Previewed' ? 'sql-ok' : 'sql-error'} role="status">{resultLabel(status)}{result?.error && <p>{result.error.code}: {result.error.message}</p>}</div>}
    </div>
    <SqlSnapshot snapshot={result} definition={spec.definition} seed={spec.seed} />
    <details className="sql-card"><summary>{'\u0418\u0441\u0442\u043e\u0440\u0438\u044f \u043f\u0440\u043e\u0432\u0435\u0440\u043e\u043a'}</summary>{history.map(item => <div key={item.id} className="sql-toolbar" style={{ marginTop: '.7rem' }}><span>{new Date(item.createdAt || item.createdAtUtc).toLocaleString()}</span><span className="sql-badge">{resultLabel(item.status || item.verdict)}</span><span className="sql-muted">{spec.targets.find(t => t.engineProfileId === item.sqlEngineProfileId)?.displayName || item.executionTarget?.slice(0,12)}</span><button type="button" disabled={busy} onClick={async () => { try { const details = await getMySolutionDetails(item.id); setResult(ownSnapshot(details.result)); setStatus(details.status || details.verdict); } catch(e) { setError(getApiErrorMessage(e)); } }}>{'\u0420\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442'}</button></div>)}</details>
  </div>;
}
