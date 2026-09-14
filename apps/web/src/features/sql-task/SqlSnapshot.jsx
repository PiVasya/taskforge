import React from 'react';
import { Card } from '../../components/ui';
import { cellText } from './sqlModel';

function Rows({ result }) {
  if (!result) return null;
  const columns = result.columns || [];
  const rows = result.rows || [];

  return (
    <div className="sql-result-table-wrap">
      <table className="sql-result-table">
        <thead>
          <tr>{columns.map((column, index) => <th key={index}>{typeof column === 'object' ? column.name : column}</th>)}</tr>
        </thead>
        <tbody>
          {rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {row.map((cell, cellIndex) => (
                <td key={cellIndex} className={cell === null || cell?.type === 'null' ? 'sql-null' : ''}>
                  {cellText(cell)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {!rows.length ? <div className="sql-empty">Нет строк</div> : null}
      {result.truncated ? <div className="sql-result-note">Показана часть результата</div> : null}
    </div>
  );
}

export default function SqlSnapshot({ snapshot }) {
  if (!snapshot) return null;
  const results = snapshot.results || [];
  const hasResultRows = results.length > 0;
  const affectedRows = Number(snapshot.affectedRows || 0);

  return (
    <Card className="sql-result-card">
      {snapshot.previewError ? <div className="sql-inline-error">{snapshot.previewError.message}</div> : null}
      {hasResultRows ? results.map((result, index) => (
        <div key={index} className={index ? 'sql-result-block sql-result-block--separated' : 'sql-result-block'}>
          {results.length > 1 ? <div className="sql-result-label">Результат {result.statement ?? index + 1}</div> : null}
          <Rows result={result} />
        </div>
      )) : null}
      {!hasResultRows && !snapshot.previewError ? (
        <div className="sql-result-summary">Изменено строк: {affectedRows}</div>
      ) : null}
    </Card>
  );
}
