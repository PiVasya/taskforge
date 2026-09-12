// Portable authoring documents. Numeric seed values remain strings: JS Number would lose bigint/decimal precision.
export const LOGICAL_TYPES = ['integer', 'bigint', 'decimal', 'string', 'text', 'boolean', 'date', 'datetime', 'uuid', 'binary'];
export const ENGINES = ['postgresql', 'mysql', 'sqlite'];
export const ACTIONS = ['no_action', 'restrict', 'cascade', 'set_null'];
export const PENDING = new Set(['preparing', 'queued', 'running', 'pending']);
export const isPending = status => PENDING.has(String(status || '').toLowerCase());
export const freshDataset = () => ({ definition: { tables: [] }, seed: {}, engineOverrides: {} });
export const freshSpec = () => ({ datasetVersionId: null, mode: 'result', starterSql: '', referenceSql: '',
  allowMultipleStatements: false, resultComparisonSettings: { orderMatters: false, columnNamesMatter: true,
    duplicatesMatter: true, caseSensitive: true, numericTolerance: 0 }, stateCheckSettings: { tables: null },
  schemaCheckSettings: { tables: null, constraintNamesMatter: false, defaultsMatter: true, indexesMatter: true },
  limits: { timeoutMs: 5000, maxRows: 1000, previewRows: 200, maxBytes: 1048576, maxStatements: 20 }, targets: [], concurrencyStamp: null });
export const newColumn = name => ({ name, type: 'integer', nullable: false, identity: false, default: null });
export const newTable = name => ({ name, columns: [newColumn('id')], primaryKey: ['id'], unique: [], foreignKeys: [], indexes: [] });
export const clone = value => JSON.parse(JSON.stringify(value));
export const list = value => String(value || '').split(',').map(x => x.trim()).filter(Boolean);
export const cellText = cell => cell === null || cell?.type === 'null' ? 'NULL' : typeof cell === 'object' ? String(cell.value ?? '') : String(cell ?? '');
export function identifierError(name) {
  return !/^[a-z][a-z0-9_]{0,47}$/.test(name) || /^(sqlite_|tfq_)/.test(name);
}
export function renameTable(doc, index, name) {
  const next = clone(doc), old = next.definition.tables[index].name;
  next.definition.tables[index].name = name;
  if (Object.hasOwn(next.seed, old)) { next.seed[name] = next.seed[old]; delete next.seed[old]; }
  next.definition.tables.forEach(t => t.foreignKeys?.forEach(f => { if (f.referenceTable === old) f.referenceTable = name; }));
  for (const engine of Object.values(next.engineOverrides || {})) {
    engine.columns = Object.fromEntries(Object.entries(engine.columns || {}).map(([k, v]) => [k.startsWith(`${old}.`) ? `${name}.${k.slice(old.length + 1)}` : k, v]));
  }
  return next;
}
export function renameColumn(doc, tableIndex, columnIndex, name) {
  const next = clone(doc), table = next.definition.tables[tableIndex], old = table.columns[columnIndex].name;
  table.columns[columnIndex].name = name;
  const replace = cols => (cols || []).map(x => x === old ? name : x);
  table.primaryKey = replace(table.primaryKey); table.unique = (table.unique || []).map(replace);
  (table.indexes || []).forEach(x => { x.columns = replace(x.columns); });
  (table.foreignKeys || []).forEach(x => { x.columns = replace(x.columns); });
  next.definition.tables.forEach(t => (t.foreignKeys || []).forEach(f => { if (f.referenceTable === table.name) f.referenceColumns = replace(f.referenceColumns); }));
  (next.seed[table.name] || []).forEach(row => { if (Object.hasOwn(row, old)) { row[name] = row[old]; delete row[old]; } });
  for (const engine of Object.values(next.engineOverrides || {})) {
    const key = `${table.name}.${old}`;
    if (Object.hasOwn(engine.columns || {}, key)) { engine.columns[`${table.name}.${name}`] = engine.columns[key]; delete engine.columns[key]; }
  }
  return next;
}
export function datasetIssues(doc) {
  const errors = [], tables = doc.definition.tables;
  const names = new Set();
  tables.forEach(table => {
    if (identifierError(table.name) || names.has(table.name)) errors.push(`Table: ${table.name}`);
    names.add(table.name);
    if (!table.columns.length) errors.push(`${table.name}: add a column`);
    const cols = new Set();
    table.columns.forEach(c => { if (identifierError(c.name) || cols.has(c.name)) errors.push(`${table.name}.${c.name}`); cols.add(c.name); });
    for (const f of table.foreignKeys || []) if (!tables.some(t => t.name === f.referenceTable)) errors.push(`FK ${f.name}: unknown target`);
  });
  return errors;
}

export function toggleEngineTargets(targets, engineProfileId, checked) {
  const current = Array.isArray(targets) ? targets : [];
  if (!checked) return current.filter(t => t.engineProfileId !== engineProfileId);
  const index = current.findIndex(t => t.engineProfileId === engineProfileId);
  if (index >= 0) return current.map((t, i) => i === index ? { ...t, enabled: true } : t);
  return [...current, { engineProfileId, enabled: true, sort: current.length, starterSqlOverride: null, referenceSqlOverride: null }];
}

export async function refreshDatasetCatalogBestEffort(load, apply) {
  try {
    const value = await load();
    apply(value);
    return true;
  } catch {
    return false;
  }
}
export function editorInput(spec, versionId, stamp) {
  return { ...spec, datasetVersionId: versionId, concurrencyStamp: stamp ?? null,
    targets: spec.targets.map((t, sort) => ({ ...t, sort })) };
}
export function ownSnapshot(result) { return result?.sql || result || null; }
export function resultLabel(status) {
  return ({ Accepted: '\u041f\u0440\u0438\u043d\u044f\u0442\u043e', WrongAnswer: '\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043e\u0442\u0432\u0435\u0442', RuntimeError: '\u041e\u0448\u0438\u0431\u043a\u0430 SQL',
    TimeLimitExceeded: '\u041b\u0438\u043c\u0438\u0442 \u0432\u0440\u0435\u043c\u0435\u043d\u0438', OutputLimitExceeded: '\u041b\u0438\u043c\u0438\u0442 \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442\u0430',
    JudgeUnavailable: '\u0414\u0432\u0438\u0436\u043e\u043a \u0432\u0440\u0435\u043c\u0435\u043d\u043d\u043e \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d', Previewed: '\u0417\u0430\u043f\u0443\u0441\u043a \u0437\u0430\u0432\u0435\u0440\u0448\u0451\u043d' })[status] || status;
}
