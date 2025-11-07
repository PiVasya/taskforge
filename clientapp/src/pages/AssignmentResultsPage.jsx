import React from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui/Card';

function Block({ title, children }) {
  return (
    <Card>
      <div className="font-semibold mb-2">{title}</div>
      {children}
    </Card>
  );
}

function TestsList({ results = [] }) {
  return (
    <div className="space-y-3">
      {results.map((r, idx) => (
        <div key={idx} className="p-3 border rounded">
          <div className="flex items-center justify-between">
            <div className="font-semibold">{r.name || `Test #${idx + 1}`}</div>
            <div className="text-xl">{r.passed ? '✅' : '❌'}</div>
          </div>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-3 mt-2">
            <div>
              <div className="font-semibold">Вход</div>
              <pre className="whitespace-pre-wrap text-xs p-2 border rounded">{r.input || ''}</pre>
            </div>
            <div>
              <div className="font-semibold">Ожидаемый вывод</div>
              <pre className="whitespace-pre-wrap text-xs p-2 border rounded">{r.expectedOutput || ''}</pre>
            </div>
            <div>
              <div className="font-semibold">Фактический вывод / ошибка</div>
              <pre className="whitespace-pre-wrap text-xs p-2 border rounded">
                {(r.actualOutput && String(r.actualOutput)) || (r.stderr && String(r.stderr)) || ''}
              </pre>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

export default function AssignmentResultsPage() {
  const { assignmentId } = useParams();
  const { state } = useLocation() || {};
  const smoke = state?.smoke || null;
  const result = state?.result || null;

  return (
    <Layout>
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <Link to={`/assignment/${assignmentId}`} className="btn">← Назад к заданию</Link>
        </div>
      </div>

      <div className="space-y-5">
        {smoke && (
          <Block title="Смок-проверка">
            {smoke.compileError && (
              <div className="mb-3">
                <div className="font-semibold">Ошибка компиляции</div>
                <pre className="whitespace-pre-wrap text-sm">{smoke.compileError}</pre>
              </div>
            )}
            <TestsList results={smoke.results || []} />
          </Block>
        )}

        {result && (
          <Block title="Полная проверка">
            <div className="text-sm mb-3">
              Пройдено: {result.passed} • Провалено: {result.failed}
              {typeof result.passedAll !== 'undefined' && (
                <> • Все тесты пройдены: {result.passedAll ? 'да' : 'нет'}</>
              )}
            </div>
            {/* Если хочешь, можно сюда подгрузить подробности тестов по API последнего сабмита */}
            <div className="text-slate-500 text-sm">
              Для показа детальных входов/выходов по полному прогону
              можно добавить отдельный API на бэке (последний сабмит + его тест-лог).
            </div>
          </Block>
        )}

        {!smoke && !result && (
          <Card>
            <div className="text-slate-500">Данных для отображения пока нет.</div>
          </Card>
        )}
      </div>
    </Layout>
  );
}
