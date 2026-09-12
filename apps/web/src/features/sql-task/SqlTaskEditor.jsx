import React, { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import CodeEditor from '../../components/CodeEditor';
import { getApiErrorMessage } from '../../api/http';
import * as api from '../../api/sqlTasks';
import { clone, datasetIssues, editorInput, freshDataset, freshSpec, list, refreshDatasetCatalogBestEffort, toggleEngineTargets, validationReadyForTargets } from './sqlModel';
import SqlDatasetEditor, { Check, F, NameInput } from './SqlDatasetEditor';
import './sql-task.css';

function invalidateSelection(selectionRef) {
  selectionRef.current += 1;
}

const SqlTaskEditor = forwardRef(function SqlTaskEditor({ assignmentId, onPublished }, ref) {
  const [view, setView] = useState(null), [spec, setSpec] = useState(freshSpec);
  const [datasets, setDatasets] = useState([]), [profiles, setProfiles] = useState([]), [runtime, setRuntime] = useState(null);
  const [datasetInfo, setDatasetInfo] = useState(null), [datasetId, setDatasetId] = useState(''), [versionId, setVersionId] = useState('');
  const [doc, setDoc] = useState(freshDataset), [name, setName] = useState(''), [description, setDescription] = useState(''), [scope, setScope] = useState('private');
  const [datasetDirty, setDatasetDirty] = useState(false), [specDirty, setSpecDirty] = useState(false);
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const operation = useRef(false), mounted = useRef(true), selection = useRef(0);
  const current = useRef(null);
  current.current = { datasetDirty, specDirty, busy };
  const changeSpec = patch => { setSpec(old => ({ ...old, ...patch })); setSpecDirty(true); setMessage(''); };
  const changeDoc = value => { setDoc(value); setDatasetDirty(true); setMessage(''); };
  const applyView = value => { setView(value); if (value?.spec) setSpec(value.spec); setSpecDirty(false); };
  const loadDataset = async (id, preferredVersion) => {
    const seq = ++selection.current;
    const info = await api.sqlDataset(id);
    const selected = preferredVersion || info.versions?.[0]?.id || '';
    const version = selected ? await api.sqlDatasetVersion(id, selected) : null;
    if (!mounted.current || seq !== selection.current) return;
    setDatasetId(id); setDatasetInfo(info); setVersionId(selected);
    setName(info.dataset.name); setDescription(info.dataset.description || ''); setScope(info.dataset.accessScope);
    setDoc(version ? { definition: version.definition, seed: version.seed, engineOverrides: version.engineOverrides || {} } : freshDataset());
    setDatasetDirty(false);
  };
  useEffect(() => {
    mounted.current = true;
    let alive = true;
    (async () => {
      try {
        const [edit, ds, engines] = await Promise.all([api.sqlEdit(assignmentId), api.sqlDatasets(), api.sqlEngines()]);
        if (!alive) return;
        applyView(edit); setDatasets(ds); setProfiles(engines);
        if (edit.datasetId) await loadDataset(edit.datasetId, edit.spec.datasetVersionId);
      } catch (e) { if (alive) setError(getApiErrorMessage(e)); }
      finally { if (alive) setLoading(false); }
    })();
    return () => { alive = false; mounted.current = false; invalidateSelection(selection); };
    // The editor is keyed by assignment; an in-flight change must not overwrite dirty fields.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assignmentId]);
  useEffect(() => {
    let stopped = false;
    const refresh = async () => {
      try {
        const status = await api.sqlRuntime(); if (!stopped) setRuntime(status);
        const engines = await api.sqlEngines(); if (!stopped) setProfiles(engines);
        if (view?.draftVersionId && !operation.current) { const latest = await api.sqlEdit(assignmentId); if (!stopped) setView(old => {
          if (old?.draftVersionId !== latest.draftVersionId) return old;
          return { ...old, validation: latest.validation, profiles: latest.profiles };
        }); }
      } catch { /* Read-only polling does not discard the author's unsaved state. */ }
    };
    void refresh(); const timer = setInterval(refresh, 3000);
    return () => { stopped = true; clearInterval(timer); };
  }, [assignmentId, view?.draftVersionId]);
  useEffect(() => {
    const guard = event => { if (current.current.datasetDirty || current.current.specDirty) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', guard); return () => window.removeEventListener('beforeunload', guard);
  }, []);
  const allProfiles = [...new Map([...(profiles || []), ...(view?.profiles || [])].map(p => [p.id,p])).values()];
  const profileFor = id => allProfiles.find(p => p.id === id);
  const online = new Set((runtime?.workers || []).flatMap(w => w.targets || []));
  const upsertDatasetSummary = dataset => setDatasets(old => [dataset, ...old.filter(item => item.id !== dataset.id)]);
  const saveDataset = async () => {
    if (!name.trim()) throw new Error('\u0417\u0430\u0434\u0430\u0439 \u043d\u0430\u0437\u0432\u0430\u043d\u0438\u0435 dataset.');
    const issues = datasetIssues(doc); if (issues.length) throw new Error(issues.join('; '));
    let id = datasetId, info = datasetInfo;
    if (!id) {
      const dataset = await api.createSqlDataset({ name, description, accessScope: scope });
      id = dataset.id; info = { dataset, versions: [] }; setDatasetId(id); setDatasetInfo(info); upsertDatasetSummary(dataset);
    } else if (info.dataset.name !== name || (info.dataset.description || '') !== description || info.dataset.accessScope !== scope) {
      const dataset = await api.updateSqlDataset(id, { name, description, accessScope: scope, isArchived: false, concurrencyStamp: info.dataset.concurrencyStamp });
      info = { ...info, dataset }; setDatasetInfo(info); upsertDatasetSummary(dataset);
    }
    const version = await api.createSqlDatasetVersion(id, { ...doc, concurrencyStamp: info.dataset.concurrencyStamp });
    const updated = { dataset: { ...info.dataset, concurrencyStamp: version.concurrencyStamp }, versions: [version, ...(info.versions || [])] };
    setDatasetInfo(updated); setVersionId(version.id); setDatasetDirty(false);
    void refreshDatasetCatalogBestEffort(api.sqlDatasets, value => { if (mounted.current) setDatasets(value); });
    return version.id;
  };
  const save = async () => {
    if (operation.current) throw new Error('\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435 \u0443\u0436\u0435 \u0438\u0434\u0451\u0442.');
    if (!spec.targets.some(t => t.enabled)) throw new Error('\u0412\u044b\u0431\u0435\u0440\u0438 \u0445\u043e\u0442\u044f \u0431\u044b \u043e\u0434\u0438\u043d \u0434\u0432\u0438\u0436\u043e\u043a.');
    operation.current = true; setBusy(true); setError(''); setMessage('');
    try {
      const version = datasetDirty || !versionId ? await saveDataset() : versionId;
      if (!specDirty && view?.spec?.datasetVersionId === version) return view;
      const saved = await api.saveSqlEdit(assignmentId, editorInput(spec,version,view?.concurrencyStamp));
      applyView(saved); setMessage('\u0427\u0435\u0440\u043d\u043e\u0432\u0438\u043a \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d. \u0412\u0430\u043b\u0438\u0434\u0430\u0446\u0438\u044f \u044d\u0442\u0430\u043b\u043e\u043d\u0430 \u0438\u0434\u0451\u0442 \u0432 \u0444\u043e\u043d\u0435.');
      return saved;
    } catch (e) { setError(getApiErrorMessage(e) || e.message); throw e; }
    finally { operation.current = false; if (mounted.current) setBusy(false); }
  };
  useImperativeHandle(ref, () => ({ save }));
  const publish = async () => {
    setBusy(true); operation.current = true; setError('');
    try {
      // Publication is a server-state operation. Re-read the saved draft so a delayed
      // validation/poll response can never publish a stale subset of engine targets.
      const latest = await api.sqlEdit(assignmentId);
      setView(latest);
      if (!validationReadyForTargets(latest?.spec?.targets, latest?.validation)) {
        setError('Все включённые движки должны успешно пройти проверку перед публикацией.');
        return;
      }
      const saved = await api.publishSqlEdit(assignmentId, { versionId: latest.draftVersionId, concurrencyStamp: latest.concurrencyStamp });
      setView(saved); setMessage('\u0412\u0435\u0440\u0441\u0438\u044f \u043e\u043f\u0443\u0431\u043b\u0438\u043a\u043e\u0432\u0430\u043d\u0430. \u0422\u0435\u043f\u0435\u0440\u044c \u0437\u0430\u0434\u0430\u043d\u0438\u0435 \u043c\u043e\u0436\u043d\u043e \u0441\u0434\u0435\u043b\u0430\u0442\u044c \u0432\u0438\u0434\u0438\u043c\u044b\u043c.'); onPublished?.();
    } catch(e) { setError(getApiErrorMessage(e)); }
    finally { setBusy(false); operation.current = false; }
  };
  const chooseDataset = async id => {
    if (datasetDirty && !window.confirm('\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c \u043d\u0435\u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d\u043d\u044b\u0435 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f dataset?')) return;
    setBusy(true); setError('');
    try {
      if (!id) { selection.current++; setDatasetId(''); setDatasetInfo(null); setVersionId(''); setName(''); setDescription(''); setScope('private'); setDoc(freshDataset()); setDatasetDirty(true); }
      else await loadDataset(id);
      setSpecDirty(true);
    } catch(e) { setError(getApiErrorMessage(e)); } finally { setBusy(false); }
  };
  const targets = spec.targets || [];
  const toggleEngine = (id, checked) => changeSpec({ targets: toggleEngineTargets(targets, id, checked) });
  const changeTarget = (index, patch) => changeSpec({ targets: targets.map((t,i) => i === index ? { ...t,...patch } : t) });
  const ready = validationReadyForTargets(targets, view?.validation);
  const dirty = datasetDirty || specDirty || (!!versionId && view?.spec?.datasetVersionId !== versionId);
  if (loading) return <div className="sql-task"><div className="sql-card" role="status">{'\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430 SQL-\u0440\u0435\u0434\u0430\u043a\u0442\u043e\u0440\u0430\u2026'}</div></div>;
  return <div className="sql-task">
    <div className="sql-card"><div className="sql-toolbar"><h2>{'SQL / \u0411\u0430\u0437\u0430 \u0434\u0430\u043d\u043d\u044b\u0445'}</h2><span className="sql-badge">{view?.publishedVersionId ? '\u0415\u0441\u0442\u044c \u043e\u043f\u0443\u0431\u043b\u0438\u043a\u043e\u0432\u0430\u043d\u043d\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f' : '\u0427\u0435\u0440\u043d\u043e\u0432\u0438\u043a'}</span></div>
      <p className="sql-muted">{'\u0418\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f \u0441\u043e\u0437\u0434\u0430\u044e\u0442 \u043d\u043e\u0432\u044b\u0435 \u0432\u0435\u0440\u0441\u0438\u0438. \u0414\u0440\u0443\u0433\u0438\u0435 \u0437\u0430\u0434\u0430\u043d\u0438\u044f \u043d\u0430 \u0442\u043e\u043c \u0436\u0435 dataset \u043d\u0435 \u043c\u0435\u043d\u044f\u044e\u0442\u0441\u044f. \u0423\u0447\u0435\u043d\u0438\u043a\u0438 \u0432\u0438\u0434\u044f\u0442 \u0442\u043e\u043b\u044c\u043a\u043e \u043e\u043f\u0443\u0431\u043b\u0438\u043a\u043e\u0432\u0430\u043d\u043d\u0443\u044e \u0432\u0435\u0440\u0441\u0438\u044e.'}</p>
      {error && <div className="sql-error" role="alert">{error}</div>}{message && <div className="sql-ok" role="status">{message}</div>}
      <div className="sql-grid"><F label="Dataset"><select disabled={busy} value={datasetId} onChange={e => void chooseDataset(e.target.value)}><option value="">{'+ \u041d\u043e\u0432\u044b\u0439 dataset'}</option>{datasets.filter(d => !d.isArchived || d.id === datasetId).map(d => <option key={d.id} value={d.id}>{d.name}{d.isArchived ? ' [archived]' : ''}</option>)}</select></F>
        <F label={'\u0412\u0435\u0440\u0441\u0438\u044f dataset'}><select disabled={busy || !datasetId} value={versionId} onChange={async e => { const id = e.target.value; if (datasetDirty && !window.confirm('\u0421\u0431\u0440\u043e\u0441\u0438\u0442\u044c \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f?')) return; setBusy(true); try { await loadDataset(datasetId,id); setSpecDirty(true); } catch(e) { setError(getApiErrorMessage(e)); } finally { setBusy(false); } }}><option value="">{'\u0415\u0449\u0451 \u043d\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d'}</option>{datasetInfo?.versions?.map(v => <option key={v.id} value={v.id}>v{v.version} / {v.contentHash?.slice(0,12)}</option>)}</select></F></div>
      <div className="sql-grid" style={{ marginTop: '.8rem' }}><F label={'\u041d\u0430\u0437\u0432\u0430\u043d\u0438\u0435 dataset'}><input maxLength={200} disabled={busy} value={name} onChange={e => { setName(e.target.value); setDatasetDirty(true); }} /></F><F label={'\u0414\u043e\u0441\u0442\u0443\u043f'}><select disabled={busy} value={scope} onChange={e => { setScope(e.target.value); setDatasetDirty(true); }}><option value="private">{'\u041c\u043e\u0439 dataset'}</option><option value="editor-library">{'\u041e\u0431\u0449\u0430\u044f \u0431\u0438\u0431\u043b\u0438\u043e\u0442\u0435\u043a\u0430 \u0440\u0435\u0434\u0430\u043a\u0442\u043e\u0440\u043e\u0432'}</option></select></F></div>
      <F label={'\u041e\u043f\u0438\u0441\u0430\u043d\u0438\u0435 dataset'}><input value={description} maxLength={2000} disabled={busy} onChange={e => { setDescription(e.target.value); setDatasetDirty(true); }} /></F>
      {datasetId && <button type="button" disabled={busy} onClick={() => { setDatasetId(''); setDatasetInfo(null); setVersionId(''); setScope('private'); setName(`${name} (copy)`); setDatasetDirty(true); setSpecDirty(true); }}>{'\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u043d\u0435\u0437\u0430\u0432\u0438\u0441\u0438\u043c\u0443\u044e \u043a\u043e\u043f\u0438\u044e'}</button>}
    </div>
    <div className="sql-card"><SqlDatasetEditor value={doc} onChange={changeDoc} disabled={busy} /></div>
    <div className="sql-card"><h3>{'\u0414\u0432\u0438\u0436\u043a\u0438 \u0438 \u043f\u0440\u043e\u0432\u0435\u0440\u043a\u0430'}</h3>
      {!allProfiles.length && <div className="sql-error">{'\u0414\u0432\u0438\u0436\u043a\u0438 \u043f\u043e\u043a\u0430 \u043d\u0435 \u0437\u0430\u0440\u0435\u0433\u0438\u0441\u0442\u0440\u0438\u0440\u043e\u0432\u0430\u043d\u044b. \u041d\u0443\u0436\u0435\u043d \u0440\u0430\u0431\u043e\u0442\u0430\u044e\u0449\u0438\u0439 sql-worker.'}</div>}
      <div className="sql-status-list">{allProfiles.map(p => <div className="sql-toolbar" key={p.id}><Check label={`${p.displayName} / ${p.fingerprint.slice(0,8)}`} value={targets.some(t => t.engineProfileId === p.id && t.enabled !== false)} onChange={v => toggleEngine(p.id,v)} disabled={busy} /><span className="sql-badge">{online.has(p.fingerprint) ? 'ONLINE' : 'OFFLINE'}</span></div>)}</div>
      <F label={'\u0420\u0435\u0436\u0438\u043c'}><select value={spec.mode} disabled={busy} onChange={e => changeSpec({ mode:e.target.value,allowMultipleStatements:e.target.value !== 'result' })}><option value="result">{'Result \u2014 \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442 SELECT'}</option><option value="state">{'State \u2014 \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u0434\u0430\u043d\u043d\u044b\u0445'}</option><option value="schema">{'Schema \u2014 \u0441\u0442\u0440\u0443\u043a\u0442\u0443\u0440\u0430 \u0411\u0414'}</option></select></F>
      <Check label={'\u0420\u0430\u0437\u0440\u0435\u0448\u0438\u0442\u044c \u043d\u0435\u0441\u043a\u043e\u043b\u044c\u043a\u043e statements'} value={spec.allowMultipleStatements} disabled={busy} onChange={v => changeSpec({ allowMultipleStatements:v })} />
      <p className="sql-muted">{'\u0421\u043a\u0440\u0438\u043f\u0442 \u0438\u0441\u043f\u043e\u043b\u043d\u044f\u0435\u0442\u0441\u044f \u043f\u043e \u043f\u043e\u0440\u044f\u0434\u043a\u0443 \u0434\u043e \u043f\u0435\u0440\u0432\u043e\u0439 \u043e\u0448\u0438\u0431\u043a\u0438. Result \u0441\u0440\u0430\u0432\u043d\u0438\u0432\u0430\u0435\u0442 \u043f\u043e\u0441\u043b\u0435\u0434\u043d\u0438\u0439 result set. BEGIN / COMMIT, \u0430\u0434\u043c\u0438\u043d\u0438\u0441\u0442\u0440\u0438\u0440\u043e\u0432\u0430\u043d\u0438\u0435 \u0441\u0435\u0440\u0432\u0435\u0440\u0430 \u0438 \u0432\u043d\u0435\u0448\u043d\u0438\u0435 \u0444\u0430\u0439\u043b\u044b \u043d\u0435 \u043f\u043e\u0434\u0434\u0435\u0440\u0436\u0438\u0432\u0430\u044e\u0442\u0441\u044f.'}</p>
      <div className="sql-grid"><div><h4>{'\u0421\u0442\u0430\u0440\u0442\u043e\u0432\u044b\u0439 SQL \u0443\u0447\u0435\u043d\u0438\u043a\u0430'}</h4><CodeEditor language="sql" value={spec.starterSql || ''} onChange={v => changeSpec({starterSql:v || ''})} modelPath={`sql-starter-${assignmentId}`} height={220} readOnly={busy} /></div><div><h4>{'\u042d\u0442\u0430\u043b\u043e\u043d SQL \u2014 \u0441\u043a\u0440\u044b\u0442 \u043e\u0442 \u0443\u0447\u0435\u043d\u0438\u043a\u0430'}</h4><CodeEditor language="sql" value={spec.referenceSql || ''} onChange={v => changeSpec({referenceSql:v || ''})} modelPath={`sql-reference-${assignmentId}`} height={220} readOnly={busy} /></div></div>
      {targets.map((target,i) => <details key={target.engineProfileId}><summary>{profileFor(target.engineProfileId)?.displayName || target.engineProfileId} {'\u2014 \u043f\u0435\u0440\u0435\u043e\u043f\u0440\u0435\u0434\u0435\u043b\u0435\u043d\u0438\u044f'}</summary>
        <div className="sql-grid">{['starterSqlOverride','referenceSqlOverride'].map(field => <div key={field}><Check label={field === 'starterSqlOverride' ? '\u0421\u0432\u043e\u0439 \u0441\u0442\u0430\u0440\u0442\u043e\u0432\u044b\u0439 SQL' : '\u0421\u0432\u043e\u0439 \u044d\u0442\u0430\u043b\u043e\u043d SQL'} disabled={busy} value={target[field] !== null && target[field] !== undefined} onChange={v => changeTarget(i,{[field]:v ? '' : null})} />{target[field] !== null && target[field] !== undefined && <CodeEditor language="sql" value={target[field]} onChange={v => changeTarget(i,{[field]:v || ''})} modelPath={`sql-${assignmentId}-${target.engineProfileId}-${field}`} height={180} readOnly={busy} />}</div>)}</div>
        <Check label={'\u0421\u0432\u043e\u0438 \u043f\u0440\u0430\u0432\u0438\u043b\u0430 \u0441\u0440\u0430\u0432\u043d\u0435\u043d\u0438\u044f'} value={!!target.resultComparisonSettingsOverride} disabled={busy} onChange={v => changeTarget(i,{resultComparisonSettingsOverride:v ? clone(spec.resultComparisonSettings) : null})} />
        {target.resultComparisonSettingsOverride && <Comparison value={target.resultComparisonSettingsOverride} onChange={v => changeTarget(i,{resultComparisonSettingsOverride:v})} disabled={busy} />}
      </details>)}
      <details open><summary>{'\u041f\u0440\u0430\u0432\u0438\u043b\u0430 \u0441\u0440\u0430\u0432\u043d\u0435\u043d\u0438\u044f \u0438 \u043b\u0438\u043c\u0438\u0442\u044b'}</summary><Comparison value={spec.resultComparisonSettings} onChange={v => changeSpec({resultComparisonSettings:v})} disabled={busy} />
        {spec.mode !== 'result' && <F label={'\u041f\u0440\u043e\u0432\u0435\u0440\u044f\u0435\u043c\u044b\u0435 \u0442\u0430\u0431\u043b\u0438\u0446\u044b (\u043f\u0443\u0441\u0442\u043e = \u0432\u0441\u0435)'}><NameInput value={(spec[spec.mode === 'state' ? 'stateCheckSettings' : 'schemaCheckSettings'].tables || []).join(', ')} onCommit={v => { const field = spec.mode === 'state' ? 'stateCheckSettings' : 'schemaCheckSettings'; changeSpec({[field]:{...spec[field],tables:list(v).length ? list(v) : null}}); }} disabled={busy} /></F>}
        {spec.mode === 'schema' && ['constraintNamesMatter','defaultsMatter','indexesMatter'].map(k => <Check key={k} label={k} value={spec.schemaCheckSettings[k]} disabled={busy} onChange={v => changeSpec({schemaCheckSettings:{...spec.schemaCheckSettings,[k]:v}})} />)}
        <div className="sql-grid">{[['timeoutMs',100,10000],['maxRows',1,1000],['previewRows',1,200],['maxBytes',1024,2097152],['maxStatements',1,50]].map(([k,min,max]) => <F key={k} label={k}><input type="number" min={min} max={max} value={spec.limits[k]} disabled={busy} onChange={e => changeSpec({limits:{...spec.limits,[k]:Number(e.target.value)}})} /></F>)}</div>
      </details>
    </div>
    <div className="sql-card"><h3>{'\u0412\u0430\u043b\u0438\u0434\u0430\u0446\u0438\u044f \u0438 \u043f\u0443\u0431\u043b\u0438\u043a\u0430\u0446\u0438\u044f'}</h3>
      {dirty && <p className="sql-muted">{'\u0415\u0441\u0442\u044c \u043d\u0435\u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d\u043d\u044b\u0435 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f. \u0421\u0442\u0430\u0442\u0443\u0441\u044b \u043d\u0438\u0436\u0435 \u043e\u0442\u043d\u043e\u0441\u044f\u0442\u0441\u044f \u043a \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d\u043d\u043e\u043c\u0443 \u0447\u0435\u0440\u043d\u043e\u0432\u0438\u043a\u0443.'}</p>}
      <div className="sql-status-list">{(view?.validation || []).map(v => <div key={v.engineProfileId} className={v.status === 'invalid' ? 'sql-error' : v.status === 'valid' ? 'sql-ok' : 'sql-muted'}><strong>{profileFor(v.engineProfileId)?.displayName || v.engineProfileId}</strong> {' / dataset: '}{v.datasetStatus}{' / reference: '}{v.status}{v.error && <div>{v.error}: {v.diagnostic?.message || v.diagnostic?.error?.message || ''}</div>}</div>)}</div>
      <div className="sql-toolbar"><button type="button" className="sql-primary" disabled={busy} onClick={() => void save().catch(() => {})}>{busy ? '\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435\u2026' : '\u0421\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c SQL-\u0447\u0435\u0440\u043d\u043e\u0432\u0438\u043a'}</button>
        <button type="button" disabled={busy || dirty || !view?.draftVersionId} onClick={async () => { setBusy(true); try { setView(await api.validateSqlEdit(assignmentId)); } catch(e) { setError(getApiErrorMessage(e)); } finally { setBusy(false); } }}>{'\u041f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u044c \u0432\u0430\u043b\u0438\u0434\u0430\u0446\u0438\u044e'}</button>
        <button type="button" disabled={busy || dirty || !ready || view?.publishedVersionId === view?.draftVersionId} onClick={() => void publish()}>{'\u041e\u043f\u0443\u0431\u043b\u0438\u043a\u043e\u0432\u0430\u0442\u044c \u0432\u0435\u0440\u0441\u0438\u044e'}</button></div>
    </div>
  </div>;
});
function Comparison({ value, onChange, disabled }) {
  return <div className="sql-toolbar" style={{ margin: '.8rem 0' }}>{[
    ['orderMatters','\u0423\u0447\u0438\u0442\u044b\u0432\u0430\u0442\u044c \u043f\u043e\u0440\u044f\u0434\u043e\u043a'],['columnNamesMatter','\u0418\u043c\u0435\u043d\u0430 \u043a\u043e\u043b\u043e\u043d\u043e\u043a'],['duplicatesMatter','\u041f\u043e\u0432\u0442\u043e\u0440\u044b'],['caseSensitive','\u0420\u0435\u0433\u0438\u0441\u0442\u0440']
  ].map(([key,label]) => <Check key={key} label={label} value={value[key]} disabled={disabled} onChange={v => onChange({...value,[key]:v})} />)}<F label={'\u0414\u043e\u043f\u0443\u0441\u043a \u0447\u0438\u0441\u0435\u043b'}><input className="sql-inline-number" type="number" step="any" min="0" max="1000000" value={value.numericTolerance} disabled={disabled} onChange={e => onChange({...value,numericTolerance:Number(e.target.value)})} /></F></div>;
}
export default SqlTaskEditor;
