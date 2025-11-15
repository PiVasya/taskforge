import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../components/Layout';
import { Card, Button, Badge } from '../components/ui';
import CodeEditor from '../components/CodeEditor';
import { getMySolutions, getMySolutionDetails } from '../api/solutions';

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
];

export default function MySolutionsPage() {
  const [solutions, setSolutions] = useState([]);
  const [filterDays, setFilterDays] = useState(null);
  const [listLoading, setListLoading] = useState(false);
  const [details, setDetails] = useState({});
  const [expandedId, setExpandedId] = useState(null);

  const loadSolutions = async () => {
    setListLoading(true);
    try {
      const list = await getMySolutions({ days: filterDays });
      setSolutions(Array.isArray(list) ? list : []);
    } catch (e) {
      console.error('Failed to load my solutions', e);
    } finally {
      setListLoading(false);
    }
  };

  useEffect(() => {
    loadSolutions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterDays]);

  const displayedSolutions = useMemo(() => {
    const list = [...solutions];
    list.sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
    return list;
  }, [solutions]);

  const handleToggleCode = async (id) => {
    if (expandedId === id) {
      setExpandedId(null);
      return;
    }
    if (!details[id]) {
      try {
        const full = await getMySolutionDetails(id);
        setDetails((prev) => ({ ...prev, [id]: full }));
      } catch (e) {
        console.error('Failed to load solution details', e);
        return;
      }
    }
    setExpandedId(id);
  };

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Мои решения</h1>

        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap gap-2 items-center">
            <span className="label">Период:</span>
            {FILTER_OPTIONS.map((opt) => (
              <Button
                key={opt.label}
                intent={filterDays === opt.value ? 'primary' : 'secondary'}
                onClick={() => setFilterDays(opt.value)}
              >
                {opt.label}
              </Button>
            ))}
          </div>

          {listLoading && <div className="text-slate-400">Загрузка…</div>}
          {!listLoading && !displayedSolutions.length && (
            <div className="text-slate-400">За выбранный период решений нет.</div>
          )}
        </Card>

        {!listLoading && displayedSolutions.length > 0 && (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-slate-400 mb-2">
              Всего попыток: {displayedSolutions.length}
            </div>

            <div className="space-y-6">
              {displayedSolutions.map((item) => {
                const full = details[item.id] || null;
                const showCode = expandedId === item.id && full;

                return (
                  <div
                    key={item.id}
                    className="border border-slate-800/40 rounded-xl p-4 bg-slate-900/40"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div>
                        <div className="font-medium">
                          {item.courseTitle} • {item.assignmentTitle}
                        </div>
                        <div className="text-xs text-slate-400">
                          {new Date(item.submittedAt).toLocaleString()} • {item.language}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center">
                        {item.passedAllTests ? (
                          <Badge intent="success">
                            Все тесты пройдены ({item.passedCount})
                          </Badge>
                        ) : (
                          <Badge intent="danger">
                            Провалено: {item.failedCount} / Пройдено: {item.passedCount}
                          </Badge>
                        )}
                        <Button size="sm" onClick={() => handleToggleCode(item.id)}>
                          {expandedId === item.id ? 'Скрыть код' : 'Показать код'}
                        </Button>
                      </div>
                    </div>

                    {showCode && (
                      <div className="mt-3 rounded-xl overflow-hidden border border-slate-700">
                        <CodeEditor
                          language={full.language || item.language}
                          value={full.submittedCode || ''}
                          readOnly
                          onChange={() => {}}
                          height={360}
                        />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </Card>
        )}
      </div>
    </Layout>
  );
}
