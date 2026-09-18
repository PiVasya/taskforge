import React from 'react';
import { Card } from '../../components/ui';
import { cellText } from './sqlModel';

function columnName(column) {
  return typeof column === 'object' ? column?.name : column;
}

function ResultTable({ result, diff, side = 'actual' }) {
  const columns = Array.isArray(result?.columns) ? result.columns : [];
  const rows = Array.isArray(result?.rows) ? result.rows : [];
  const diffRows = Array.isArray(diff?.rows) ? diff.rows : [];
  const columnMatches = Array.isArray(diff?.columnMatches) ? diff.columnMatches : [];
  const compareSide = side === 'compare';

  return (
    <div className="sql-compare-table-wrap">
      <table className="sql-compare-table">
        <thead>
          <tr>
            {columns.map((column, index) => {
              const different = columnMatches[index] === false;
              const label = columnName(column);
              return (
                <th key={`${label}-${index}`} className={different ? 'is-different' : ''}>
                  {compareSide && different ? (
                    <span className="sql-compare-hidden" title="Название столбца отличается" aria-label="Название столбца отличается">≠</span>
                  ) : (
                    <span>{label}</span>
                  )}
                  {!compareSide && different ? <span className="sql-diff-mark" title="Столбец отличается" aria-label="Столбец отличается">≠</span> : null}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, rowIndex) => {
            const hasRowDiff = rowIndex < diffRows.length;
            const rowDiff = hasRowDiff ? diffRows[rowIndex] || {} : {};
            const cells = Array.isArray(rowDiff.cells) ? rowDiff.cells : [];
            const extra = hasRowDiff && rowDiff.kind === 'extra';
            return (
              <tr key={rowIndex} className={extra ? 'is-extra' : ''}>
                {row.map((cell, cellIndex) => {
                  const hasCellDiff = hasRowDiff && cellIndex < cells.length;
                  const matches = hasCellDiff ? cells[cellIndex] !== false && !extra : null;
                  const different = matches === false;
                  return (
                    <td key={cellIndex} className={different ? 'is-different' : ''}>
                      {compareSide ? (
                        !hasCellDiff ? (
                          <span className="sql-compare-hidden" aria-label="Сравнение сокращено">…</span>
                        ) : extra ? (
                          <span className="sql-compare-hidden" aria-label="Лишняя строка">−</span>
                        ) : different ? (
                          <span className="sql-compare-hidden" aria-label="Значение отличается">≠</span>
                        ) : (
                          <span className={cell === null || cell?.type === 'null' ? 'sql-null' : ''}>{cellText(cell)}</span>
                        )
                      ) : (
                        <>
                          <span className={cell === null || cell?.type === 'null' ? 'sql-null' : ''}>{cellText(cell)}</span>
                          {different ? <span className="sql-diff-mark" aria-hidden="true">≠</span> : null}
                        </>
                      )}
                    </td>
                  );
                })}
              </tr>
            );
          })}
          {compareSide && Number(diff?.missingRows || 0) > 0 ? (
            <tr className="is-missing">
              <td colSpan={Math.max(1, columns.length)}>
                <span className="sql-compare-hidden">+{Number(diff.missingRows)} {Number(diff.missingRows) === 1 ? 'строка' : 'стр.'}</span>
              </td>
            </tr>
          ) : null}
        </tbody>
      </table>
      {!rows.length && !Number(diff?.missingRows || 0) ? <div className="sql-empty">Нет строк</div> : null}
      {compareSide && Number(diff?.missingColumns || 0) > 0 ? <div className="sql-compare-note">+{Number(diff.missingColumns)} столб.</div> : null}
      {compareSide && (Number(diff?.truncatedRows || 0) > 0 || Number(diff?.truncatedColumns || 0) > 0) ? <div className="sql-compare-note">…</div> : null}
    </div>
  );
}

function ResultPair({ title, result, diff }) {
  return (
    <section className="sql-compare-section">
      {title ? <div className="sql-compare-section-title">{title}</div> : null}
      <div className="sql-compare-grid">
        <div className="sql-compare-pane">
          <div className="sql-compare-title">Ваш результат</div>
          <ResultTable result={result} diff={diff} side="actual" />
        </div>
        <div className="sql-compare-pane">
          <div className="sql-compare-title" title="Совпавшие значения показаны; отличающиеся не раскрываются">Ожидалось</div>
          <ResultTable result={result} diff={diff} side="compare" />
        </div>
      </div>
    </section>
  );
}

function ComparisonHeader({ passed }) {
  return (
    <div className="sql-compare-verdict" role="status">
      <span className="sql-compare-verdict-mark" aria-hidden="true">{passed ? '✓' : '≠'}</span>
      <span>{passed ? 'Принято' : 'Есть отличия'}</span>
    </div>
  );
}

function SchemaComparison({ snapshot, comparison }) {
  const tables = Array.isArray(snapshot?.schema?.tables) ? snapshot.schema.tables : [];
  const databases = Array.isArray(snapshot?.databases) ? snapshot.databases : [];
  const statusByName = new Map((comparison?.schemaTables || []).map(item => [item?.name, item?.match === true]));
  const databaseStatus = new Map((comparison?.databases || []).map(item => [item?.name, item?.match === true]));
  return (
    <>
    {databases.length || Number(comparison?.missingDatabases || 0) > 0 ? (
      <div className="sql-compare-grid">
        <div className="sql-compare-pane">
          <div className="sql-compare-title">Ваши базы данных</div>
          <div className="sql-compare-table-wrap"><table className="sql-compare-table"><thead><tr><th>База данных</th></tr></thead><tbody>
            {databases.map(name => { const match = databaseStatus.get(name) !== false; return <tr key={name}><td className={match ? '' : 'is-different'}>{name}{!match ? <span className="sql-diff-mark">≠</span> : null}</td></tr>; })}
          </tbody></table>{!databases.length ? <div className="sql-empty">Нет баз данных</div> : null}</div>
        </div>
        <div className="sql-compare-pane">
          <div className="sql-compare-title">Ожидалось</div>
          <div className="sql-compare-table-wrap"><table className="sql-compare-table"><thead><tr><th>Статус</th></tr></thead><tbody>
            {databases.map(name => <tr key={name}><td><span className="sql-compare-symbol">{databaseStatus.get(name) !== false ? '=' : '≠'}</span></td></tr>)}
            {Number(comparison?.missingDatabases || 0) > 0 ? <tr className="is-missing"><td><span className="sql-compare-symbol">+{Number(comparison.missingDatabases)}</span></td></tr> : null}
          </tbody></table></div>
        </div>
      </div>
    ) : null}
    {comparison?.databaseOperationsMatch === false ? <div className="sql-compare-note">Операция CREATE/DROP DATABASE отличается от эталона.</div> : null}
    <div className="sql-compare-grid">
      <div className="sql-compare-pane">
        <div className="sql-compare-title">Ваша структура</div>
        <div className="sql-compare-table-wrap">
          <table className="sql-compare-table">
            <thead><tr><th>Таблица</th><th>Столбцы</th></tr></thead>
            <tbody>
              {tables.map((table, index) => {
                const name = String(table?.name || `table_${index + 1}`);
                const match = statusByName.get(name) !== false;
                return (
                  <tr key={name}>
                    <td className={match ? '' : 'is-different'}>{name}{!match ? <span className="sql-diff-mark">≠</span> : null}</td>
                    <td>{Array.isArray(table?.columns) ? table.columns.length : 0}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
      <div className="sql-compare-pane">
        <div className="sql-compare-title">Ожидалось</div>
        <div className="sql-compare-table-wrap">
          <table className="sql-compare-table">
            <thead><tr><th>Таблица</th><th>Статус</th></tr></thead>
            <tbody>
              {tables.map((table, index) => {
                const name = String(table?.name || `table_${index + 1}`);
                const match = statusByName.get(name) !== false;
                return <tr key={name}><td>{name}</td><td><span className="sql-compare-symbol">{match ? '=' : '≠'}</span></td></tr>;
              })}
              {Number(comparison?.missingTables || 0) > 0 ? (
                <tr className="is-missing"><td colSpan={2}><span className="sql-compare-symbol">+{Number(comparison.missingTables)}</span></td></tr>
              ) : null}
            </tbody>
          </table>
        </div>
      </div>
    </div>
    </>
  );
}

export default function SqlCheckComparison({ snapshot }) {
  const comparison = snapshot?.check?.comparison;
  if (!comparison) return null;

  if (comparison.mode === 'result') {
    const results = Array.isArray(snapshot?.results) ? snapshot.results : [];
    const result = results[results.length - 1];
    if (!result) return null;
    return (
      <Card className="sql-compare-card">
        <ComparisonHeader passed={snapshot?.check?.passed === true} />
        <ResultPair result={result} diff={comparison.result || {}} />
      </Card>
    );
  }

  if (comparison.mode === 'state') {
    const tables = Array.isArray(comparison.tables) ? comparison.tables : [];
    return (
      <Card className="sql-compare-card">
        <ComparisonHeader passed={snapshot?.check?.passed === true} />
        {tables.map((item, index) => {
          const result = snapshot?.data?.[item?.name];
          return result ? <ResultPair key={item.name || index} title={item.name} result={result} diff={item.result || {}} /> : null;
        })}
        {Number(comparison.missingTables || 0) > 0 ? <div className="sql-compare-note">+{Number(comparison.missingTables)} табл.</div> : null}
        {Number(comparison.truncatedTables || 0) > 0 ? <div className="sql-compare-note">…</div> : null}
      </Card>
    );
  }

  if (comparison.mode === 'schema') {
    return (
      <Card className="sql-compare-card">
        <ComparisonHeader passed={snapshot?.check?.passed === true} />
        <SchemaComparison snapshot={snapshot} comparison={comparison} />
      </Card>
    );
  }

  return null;
}
