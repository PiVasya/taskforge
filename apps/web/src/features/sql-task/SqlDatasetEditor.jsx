import React, { useState, useEffect } from 'react';
import { ACTIONS, ENGINES, LOGICAL_TYPES, clone, list, newColumn, newTable, renameColumn, renameTable } from './sqlModel';

export const F = ({ label, children }) => <label className="sql-field"><span>{label}</span>{children}</label>;
export const Check = ({ label, value, onChange, disabled }) => <label className="sql-check"><input type="checkbox" checked={!!value} onChange={e => onChange(e.target.checked)} disabled={disabled} />{label}</label>;
export function NameInput({ value, onCommit, ...props }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return <input {...props} value={text} onChange={e => setText(e.target.value)} onBlur={() => { if (text !== value) onCommit(text.trim()); }} />;
}
function DefaultInput({ column, value, onChange }) {
  const kind = value?.kind || 'none';
  return <div><select aria-label="Default kind" value={kind} disabled={column?.identity} onChange={e => onChange(e.target.value === 'none' ? null : { kind: e.target.value, ...(e.target.value === 'literal' ? { value: column?.type === 'boolean' ? false : '' } : {}) })}>
    <option value="none">{'\u0411\u0435\u0437 DEFAULT'}</option><option value="literal">{'\u0417\u043d\u0430\u0447\u0435\u043d\u0438\u0435'}</option><option value="current_timestamp">CURRENT_TIMESTAMP</option><option value="current_date">CURRENT_DATE</option>
  </select>{kind === 'literal' && (column?.type === 'boolean' ? <select aria-label="Default boolean" value={String(value.value)} onChange={e => onChange({ kind, value: e.target.value === 'true' })}><option>false</option><option>true</option></select> : <input aria-label="Default value" value={value.value ?? ''} onChange={e => onChange({ kind, value: e.target.value })} />)}</div>;
}
export default function SqlDatasetEditor({ value, onChange, disabled = false }) {
  const doc = value;
  const [active, setActive] = useState(0);
  const tables = doc.definition.tables;
  const index = Math.min(active, Math.max(0, tables.length - 1));
  const table = tables[index];
  const mutate = action => { const next = clone(doc); action(next, next.definition.tables[index]); onChange(next); };
  const column = (ci, patch) => mutate((next, t) => { t.columns[ci] = { ...t.columns[ci], ...patch }; });
  const removeColumn = ci => {
    if (!window.confirm('\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u043a\u043e\u043b\u043e\u043d\u043a\u0443, \u0435\u0451 \u0434\u0430\u043d\u043d\u044b\u0435 \u0438 \u0441\u0432\u044f\u0437\u0430\u043d\u043d\u044b\u0435 \u043e\u0433\u0440\u0430\u043d\u0438\u0447\u0435\u043d\u0438\u044f \u0432 \u0447\u0435\u0440\u043d\u043e\u0432\u0438\u043a\u0435?')) return;
    mutate((next, t) => {
      const name = t.columns[ci].name; t.columns.splice(ci, 1);
      t.primaryKey = (t.primaryKey || []).filter(x => x !== name);
      t.unique = (t.unique || []).filter(x => !x.includes(name));
      t.indexes = (t.indexes || []).filter(x => !x.columns.includes(name));
      t.foreignKeys = (t.foreignKeys || []).filter(x => !x.columns.includes(name));
      next.definition.tables.forEach(other => { other.foreignKeys = (other.foreignKeys || []).filter(f => f.referenceTable !== t.name || !f.referenceColumns.includes(name)); });
      (next.seed[t.name] || []).forEach(row => delete row[name]);
      Object.values(next.engineOverrides || {}).forEach(engine => { if (engine.columns) delete engine.columns[`${t.name}.${name}`]; });
    });
  };
  const deleteTable = () => {
    if (!window.confirm('\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0442\u0430\u0431\u043b\u0438\u0446\u0443 \u0438 \u0441\u0441\u044b\u043b\u043a\u0438 \u043d\u0430 \u043d\u0435\u0451 \u0432 \u0447\u0435\u0440\u043d\u043e\u0432\u0438\u043a\u0435?')) return;
    mutate((next, t) => { next.definition.tables.splice(index, 1); delete next.seed[t.name];
      next.definition.tables.forEach(other => { other.foreignKeys = (other.foreignKeys || []).filter(f => f.referenceTable !== t.name); });
      Object.values(next.engineOverrides || {}).forEach(engine => { if (engine.columns) engine.columns = Object.fromEntries(Object.entries(engine.columns).filter(([k]) => !k.startsWith(`${t.name}.`))); });
    });
    setActive(0);
  };
  const rows = table ? doc.seed[table.name] || [] : [];
  const putCell = (ri, col, mode, cell = '') => mutate((next, t) => {
    const row = next.seed[t.name][ri];
    if (mode === 'default') delete row[col.name];
    else row[col.name] = mode === 'null' ? null : col.type === 'boolean' ? cell === true || cell === 'true' : cell;
  });
  return <fieldset disabled={disabled} style={{ border: 0, padding: 0, minWidth: 0 }}>
    <div className="sql-toolbar"><h3>{'\u0422\u0430\u0431\u043b\u0438\u0446\u044b \u0438 \u0434\u0430\u043d\u043d\u044b\u0435'}</h3>
      <button type="button" disabled={tables.length >= 24} onClick={() => { const usedNames = new Set(tables.map(t => t.name)); let candidate = `table_${tables.length + 1}`; while (usedNames.has(candidate)) candidate += '_new'; const tableName = candidate; mutate(next => { next.definition.tables.push(newTable(tableName)); }); setActive(tables.length); }}>{'+ \u0422\u0430\u0431\u043b\u0438\u0446\u0430'}</button></div>
    <p className="sql-muted">{'\u041e\u0434\u043d\u0430 \u043b\u043e\u0433\u0438\u0447\u0435\u0441\u043a\u0430\u044f \u0411\u0414 \u0434\u043b\u044f \u0432\u0441\u0435\u0445 \u0434\u0432\u0438\u0436\u043a\u043e\u0432. \u0418\u043c\u0435\u043d\u0430: lower_snake_case. \u041f\u0443\u0441\u0442\u0430\u044f \u0411\u0414 \u0434\u043e\u043f\u0443\u0441\u0442\u0438\u043c\u0430 \u0434\u043b\u044f CREATE TABLE.'}</p>
    <div className="sql-tabs" role="tablist">{tables.map((t, i) => <button type="button" key={i} role="tab" aria-selected={i === index} onClick={() => setActive(i)}>{t.name}</button>)}</div>
    {table && <>
      <div className="sql-toolbar" style={{ marginTop: '.8rem' }}><F label={'\u0418\u043c\u044f \u0442\u0430\u0431\u043b\u0438\u0446\u044b'}><NameInput value={table.name} onCommit={name => onChange(renameTable(doc, index, name))} /></F><button type="button" onClick={deleteTable}>{'\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0442\u0430\u0431\u043b\u0438\u0446\u0443'}</button></div>
      <div className="sql-scroll"><table className="sql-columns"><thead><tr>{['\u041a\u043e\u043b\u043e\u043d\u043a\u0430','\u0422\u0438\u043f','\u0420\u0430\u0437\u043c\u0435\u0440 / p,s','NULL','PK','AUTO','DEFAULT',''].map((s,i) => <th key={i}>{s}</th>)}</tr></thead><tbody>
        {table.columns.map((c, ci) => <tr key={ci}><td><NameInput value={c.name} onCommit={name => onChange(renameColumn(doc,index,ci,name))} /></td>
          <td><select value={c.type} onChange={e => column(ci, { type: e.target.value, identity: false, ...(e.target.value === 'string' ? { length: 100 } : {}), ...(e.target.value === 'decimal' ? { precision: 12, scale: 2 } : {}) })}>{LOGICAL_TYPES.map(t => <option key={t}>{t}</option>)}</select></td>
          <td>{c.type === 'string' && <input type="number" min="1" max="4000" value={c.length || 100} onChange={e => column(ci, { length: Number(e.target.value) })} aria-label="String length" />}{c.type === 'decimal' && <div className="sql-toolbar"><input className="sql-inline-number" type="number" min="1" max="28" value={c.precision ?? 12} onChange={e => column(ci, { precision: Number(e.target.value) })} aria-label="Precision" /><input className="sql-inline-number" type="number" min="0" max="28" value={c.scale ?? 2} onChange={e => column(ci, { scale: Number(e.target.value) })} aria-label="Scale" /></div>}</td>
          <td><input type="checkbox" aria-label={`${c.name} nullable`} checked={c.nullable} disabled={(table.primaryKey || []).includes(c.name)} onChange={e => column(ci, { nullable: e.target.checked })} /></td>
          <td><input type="checkbox" aria-label={`${c.name} primary key`} checked={(table.primaryKey || []).includes(c.name)} onChange={e => mutate((next,t) => { t.primaryKey = e.target.checked ? [...(t.primaryKey || []),c.name] : (t.primaryKey || []).filter(n => n !== c.name); if (e.target.checked) t.columns[ci].nullable = false; if (t.primaryKey.length !== 1) t.columns.forEach(x => { x.identity = false; }); })} /></td>
          <td><input type="checkbox" aria-label={`${c.name} identity`} checked={c.identity} disabled={c.type !== 'integer' || table.primaryKey?.length !== 1 || table.primaryKey[0] !== c.name} onChange={e => column(ci, { identity: e.target.checked, default: null })} /></td>
          <td><DefaultInput column={c} value={c.default} onChange={d => column(ci, { default: d })} /></td><td><button type="button" aria-label={`Remove ${c.name}`} onClick={() => removeColumn(ci)}>&times;</button></td></tr>)}
      </tbody></table></div>
      <button type="button" style={{ marginTop: '.6rem' }} disabled={table.columns.length >= 64} onClick={() => mutate((next,t) => { const usedNames = new Set(t.columns.map(c => c.name)); let candidate = `column_${t.columns.length + 1}`; while(usedNames.has(candidate)) candidate += '_new'; const columnName = candidate; t.columns.push(newColumn(columnName)); })}>{'+ \u041a\u043e\u043b\u043e\u043d\u043a\u0430'}</button>
      <details><summary>{'UNIQUE, \u0438\u043d\u0434\u0435\u043a\u0441\u044b \u0438 \u0432\u043d\u0435\u0448\u043d\u0438\u0435 \u043a\u043b\u044e\u0447\u0438'}</summary>
        <p className="sql-muted">{'\u0421\u043e\u0441\u0442\u0430\u0432\u043d\u044b\u0435 \u043a\u043b\u044e\u0447\u0438: \u0438\u043c\u0435\u043d\u0430 \u043a\u043e\u043b\u043e\u043d\u043e\u043a \u0447\u0435\u0440\u0435\u0437 \u0437\u0430\u043f\u044f\u0442\u0443\u044e, \u0432 \u043d\u0443\u0436\u043d\u043e\u043c \u043f\u043e\u0440\u044f\u0434\u043a\u0435.'}</p>
        {(table.unique || []).map((u,i) => <div className="sql-toolbar" key={i}><F label="UNIQUE"><NameInput value={u.join(', ')} onCommit={v => mutate((n,t) => { t.unique[i] = list(v); })} /></F><button type="button" onClick={() => mutate((n,t) => t.unique.splice(i,1))}>&times;</button></div>)}
        <button type="button" onClick={() => mutate((n,t) => { t.unique = [...(t.unique || []), []]; })}>+ UNIQUE</button>
        {(table.indexes || []).map((item,i) => <div className="sql-constraint" key={i}><F label={'\u0418\u043c\u044f \u0438\u043d\u0434\u0435\u043a\u0441\u0430'}><input value={item.name} onChange={e => mutate((n,t) => { t.indexes[i].name = e.target.value; })} /></F><F label={'\u041a\u043e\u043b\u043e\u043d\u043a\u0438'}><NameInput value={item.columns.join(', ')} onCommit={v => mutate((n,t) => { t.indexes[i].columns = list(v); })} /></F><Check label="UNIQUE" value={item.unique} onChange={v => mutate((n,t) => { t.indexes[i].unique = v; })} /><button type="button" onClick={() => mutate((n,t) => t.indexes.splice(i,1))}>&times;</button></div>)}
        <button type="button" onClick={() => mutate((n,t) => { t.indexes = [...(t.indexes || []), { name: `ix_${t.name}_${(t.indexes || []).length+1}`, columns: [], unique: false }]; })}>{'+ \u0418\u043d\u0434\u0435\u043a\u0441'}</button>
        {(table.foreignKeys || []).map((fk,i) => <div className="sql-constraint" key={i}>
          <F label="FK"><input value={fk.name} onChange={e => mutate((n,t) => { t.foreignKeys[i].name = e.target.value; })} /></F>
          <F label={'\u041a\u043e\u043b\u043e\u043d\u043a\u0438'}><NameInput value={fk.columns.join(', ')} onCommit={v => mutate((n,t) => { t.foreignKeys[i].columns = list(v); })} /></F>
          <F label="REFERENCES"><select value={fk.referenceTable} onChange={e => mutate((n,t) => { t.foreignKeys[i].referenceTable = e.target.value; })}><option value="">{'\u0422\u0430\u0431\u043b\u0438\u0446\u0430'}</option>{tables.map(t => <option key={t.name}>{t.name}</option>)}</select></F>
          <F label={'\u041a\u043e\u043b\u043e\u043d\u043a\u0438 PK / UNIQUE'}><NameInput value={fk.referenceColumns.join(', ')} onCommit={v => mutate((n,t) => { t.foreignKeys[i].referenceColumns = list(v); })} /></F>
          {['onDelete','onUpdate'].map(field => <F key={field} label={field === 'onDelete' ? 'ON DELETE' : 'ON UPDATE'}><select value={fk[field]} onChange={e => mutate((n,t) => { t.foreignKeys[i][field] = e.target.value; })}>{ACTIONS.map(action => <option key={action}>{action}</option>)}</select></F>)}
          <button type="button" onClick={() => mutate((n,t) => t.foreignKeys.splice(i,1))}>&times;</button>
        </div>)}
        <button type="button" onClick={() => mutate((n,t) => { t.foreignKeys = [...(t.foreignKeys || []), { name: `fk_${t.name}_${(t.foreignKeys || []).length+1}`, columns: [], referenceTable: '', referenceColumns: [], onDelete: 'no_action', onUpdate: 'no_action' }]; })}>+ FK</button>
      </details>
      <details open><summary>{'\u041d\u0430\u0447\u0430\u043b\u044c\u043d\u044b\u0435 \u0434\u0430\u043d\u043d\u044b\u0435'} <span className="sql-badge">{rows.length} / 1000</span></summary>
        <p className="sql-muted">{'DEFAULT \u043d\u0435 \u043f\u0435\u0440\u0435\u0434\u0430\u0451\u0442 \u043a\u043e\u043b\u043e\u043d\u043a\u0443 \u043f\u0440\u0438 INSERT. NULL \u0438 \u043f\u0443\u0441\u0442\u0430\u044f \u0441\u0442\u0440\u043e\u043a\u0430 \u0440\u0430\u0437\u043b\u0438\u0447\u0430\u044e\u0442\u0441\u044f. \u0427\u0438\u0441\u043b\u0430 \u043d\u0435 \u0442\u0435\u0440\u044f\u044e\u0442 \u0442\u043e\u0447\u043d\u043e\u0441\u0442\u044c. binary: Base64.'}</p>
        {rows.length > 0 && <div className="sql-scroll"><table className="sql-seed"><thead><tr>{table.columns.map(c => <th key={c.name}>{c.name}</th>)}<th /></tr></thead><tbody>{rows.map((row,ri) => <tr key={ri}>{table.columns.map(c => {
          const mode = !Object.hasOwn(row,c.name) ? 'default' : row[c.name] === null ? 'null' : 'value';
          return <td key={c.name}><select className="sql-cell-mode" aria-label={`${ri+1} ${c.name} mode`} value={mode} onChange={e => putCell(ri,c,e.target.value,c.type === 'boolean' ? false : '')}><option value="value">{'\u0417\u043d\u0430\u0447\u0435\u043d\u0438\u0435'}</option><option value="default">DEFAULT</option><option value="null">NULL</option></select>
            {mode === 'value' && (c.type === 'boolean' ? <select value={String(row[c.name])} onChange={e => putCell(ri,c,'value',e.target.value)}><option>false</option><option>true</option></select> : <input aria-label={`${ri+1} ${c.name}`} value={String(row[c.name] ?? '')} onChange={e => putCell(ri,c,'value',e.target.value)} />)}</td>;
        })}<td><button type="button" onClick={() => mutate((n,t) => n.seed[t.name].splice(ri,1))}>&times;</button></td></tr>)}</tbody></table></div>}
        <button type="button" disabled={rows.length >= 1000} onClick={() => mutate((n,t) => { n.seed[t.name] = [...(n.seed[t.name] || []), {}]; })}>{'+ \u0421\u0442\u0440\u043e\u043a\u0430'}</button>
      </details>
      <details><summary>{'\u041f\u0435\u0440\u0435\u043e\u043f\u0440\u0435\u0434\u0435\u043b\u0435\u043d\u0438\u044f \u0442\u0438\u043f\u043e\u0432 \u043f\u043e \u0434\u0432\u0438\u0436\u043a\u0430\u043c'}</summary><p className="sql-muted">{'\u041d\u0435\u043e\u0431\u044f\u0437\u0430\u0442\u0435\u043b\u044c\u043d\u043e. \u041f\u0443\u0441\u0442\u043e\u0435 \u043f\u043e\u043b\u0435 \u043e\u0441\u0442\u0430\u0432\u043b\u044f\u0435\u0442 \u043f\u0435\u0440\u0435\u043d\u043e\u0441\u0438\u043c\u044b\u0439 \u043b\u043e\u0433\u0438\u0447\u0435\u0441\u043a\u0438\u0439 \u0442\u0438\u043f.'}</p>
        {ENGINES.map(engine => <details key={engine}><summary>{engine}</summary>{table.columns.map(c => { const key = `${table.name}.${c.name}`, mapped = doc.engineOverrides?.[engine]?.columns?.[key] || {};
          return <div key={key} className="sql-constraint"><F label={key}><input placeholder={c.type} value={mapped.type || ''} onChange={e => mutate(next => { next.engineOverrides ||= {}; next.engineOverrides[engine] ||= { columns: {} }; next.engineOverrides[engine].columns ||= {}; if (!e.target.value && !mapped.default) delete next.engineOverrides[engine].columns[key]; else next.engineOverrides[engine].columns[key] = { ...mapped, type: e.target.value || null }; })} /></F><DefaultInput column={c} value={mapped.default} onChange={d => mutate(next => { next.engineOverrides ||= {}; next.engineOverrides[engine] ||= { columns: {} }; next.engineOverrides[engine].columns ||= {}; if (!d && !mapped.type) delete next.engineOverrides[engine].columns[key]; else next.engineOverrides[engine].columns[key] = { ...mapped, default: d }; })} /></div>;
        })}</details>)}
      </details>
    </>}
  </fieldset>;
}
