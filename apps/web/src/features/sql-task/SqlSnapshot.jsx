import React, { useState } from 'react';
import { cellText } from './sqlModel';

function Rows({ result }) {
  if (!result) return null;
  return <><div className="sql-scroll"><table><thead><tr>{(result.columns || []).map((c,i) => <th key={i}>{typeof c === 'object' ? c.name : c}</th>)}</tr></thead><tbody>{(result.rows || []).map((row,ri) => <tr key={ri}>{row.map((cell,ci) => <td key={ci} className={`sql-value${cell === null || cell?.type === 'null' ? ' sql-null' : ''}`}>{cellText(cell)}</td>)}</tr>)}</tbody></table>{!result.rows?.length && <div className="sql-empty">{'\u041d\u0435\u0442 \u0441\u0442\u0440\u043e\u043a'}</div>}</div>{result.truncated && <p className="sql-muted">{'\u041f\u0440\u0435\u0434\u043f\u0440\u043e\u0441\u043c\u043e\u0442\u0440 \u043e\u0433\u0440\u0430\u043d\u0438\u0447\u0435\u043d \u043f\u043e \u0447\u0438\u0441\u043b\u0443 \u0441\u0442\u0440\u043e\u043a.'}</p>}</>;
}
function Schema({ schema }) {
  return <>{(schema?.tables || []).map(table => <details key={table.name} open><summary>{table.name} <span className="sql-badge">{table.kind || 'table'}</span></summary><div className="sql-scroll"><table><thead><tr>{['Column','Type','NULL','PK','Default'].map(h => <th key={h}>{h}</th>)}</tr></thead><tbody>{(table.columns || []).map((c,i) => <tr key={i}><td>{c.name}</td><td>{c.nativeType || c.type || c.logicalType}{c.length ? `(${c.length})` : ''}</td><td>{c.nullable ? 'NULL' : 'NOT NULL'}</td><td>{(table.primaryKey || []).includes(c.name) || c.primaryKey ? 'PK' : ''}{c.identity ? ' AUTO' : ''}</td><td className="sql-value">{typeof c.default === 'object' && c.default ? c.default.kind === 'literal' ? cellText(c.default.value) : c.default.kind : c.default || ''}</td></tr>)}</tbody></table></div>
    {(table.foreignKeys || []).map((fk,i) => <p key={`fk-${i}`} className="sql-muted">FK {fk.name || ''}: {(fk.columns || []).join(', ')} {'\u2192'} {fk.referenceTable} ({(fk.referenceColumns || []).join(', ')}) / {fk.onDelete}</p>)}
    {(table.indexes || []).map((ix,i) => <p key={`ix-${i}`} className="sql-muted">{ix.unique ? 'UNIQUE ' : ''}INDEX {ix.name}: {(ix.columns || []).map(x => typeof x === 'object' ? x.name || x.expression : x).join(', ')}</p>)}
    {(table.unique || []).map((u,i) => <p key={`u-${i}`} className="sql-muted">UNIQUE {(Array.isArray(u) ? u : u.columns || []).join(', ')}</p>)}
  </details>)}{!schema?.tables?.length && <div className="sql-empty">{'\u0422\u0430\u0431\u043b\u0438\u0446 \u043d\u0435\u0442'}</div>}</>;
}
export default function SqlSnapshot({ snapshot, definition, seed }) {
  const [tab, setTab] = useState('results');
  const initial = !snapshot;
  const schema = initial ? definition : snapshot.schema;
  const initialData = Object.fromEntries((definition?.tables || []).map(t => [t.name,{ columns:t.columns.map(c => c.name),rows:(seed?.[t.name] || []).map(row => t.columns.map(c => Object.hasOwn(row,c.name) ? row[c.name] : 'DEFAULT')) }]));
  const data = initial ? initialData : snapshot.data || {};
  return <div className="sql-card"><div className="sql-toolbar"><div className="sql-tabs" role="tablist">{[['results','\u0420\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442'],['schema','\u0421\u0445\u0435\u043c\u0430'],['data','\u0414\u0430\u043d\u043d\u044b\u0435']].map(([key,label]) => <button type="button" key={key} role="tab" aria-selected={tab === key} onClick={() => setTab(key)}>{label}</button>)}</div><span className="sql-badge">{initial ? '\u0418\u0441\u0445\u043e\u0434\u043d\u044b\u0439 dataset' : '\u041f\u043e\u0441\u043b\u0435 \u0432\u044b\u043f\u043e\u043b\u043d\u0435\u043d\u0438\u044f'}</span></div>
    {snapshot?.previewError && <p className="sql-error">{snapshot.previewError.message}</p>}
    {tab === 'results' && <>{(snapshot?.results || []).map((r,i) => <div key={i}><h4>Statement {r.statement ?? i+1}</h4><Rows result={r} /></div>)}{!snapshot?.results?.length && <div className="sql-empty">{initial ? '\u041d\u0430\u0436\u043c\u0438 Run, \u0447\u0442\u043e\u0431\u044b \u0443\u0432\u0438\u0434\u0435\u0442\u044c \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442.' : '\u0421\u043a\u0440\u0438\u043f\u0442 \u043d\u0435 \u0432\u0435\u0440\u043d\u0443\u043b result set.'}</div>}{!initial && <p className="sql-muted">{'\u0418\u0437\u043c\u0435\u043d\u0435\u043d\u043e \u0441\u0442\u0440\u043e\u043a: '}{snapshot.affectedRows ?? 0}{' / SQL: '}{snapshot.executionMs ?? '-'} ms{' / \u0418\u043d\u0441\u043f\u0435\u043a\u0446\u0438\u044f: '}{snapshot.inspectionMs ?? '-'} ms</p>}</>}
    {tab === 'schema' && <Schema schema={schema} />}
    {tab === 'data' && <>{Object.entries(data).map(([table,result]) => <details open key={table}><summary>{table}</summary><Rows result={result} /></details>)}{!Object.keys(data).length && <div className="sql-empty">{'\u041d\u0435\u0442 \u0442\u0430\u0431\u043b\u0438\u0446 \u0434\u043b\u044f \u043f\u0440\u0435\u0434\u043f\u0440\u043e\u0441\u043c\u043e\u0442\u0440\u0430'}</div>}</>}
  </div>;
}
