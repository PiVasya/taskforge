import React, { useEffect, useMemo, useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';                  // ← правильный импорт
import { getAssignment } from '../api/assignments';
import { submitSolution } from '../api/solutions';
import { runTests as runCompilerTests } from '../api/compiler';
import CodeEditor from '../components/CodeEditor';
import { useNotify } from '../components/notify/NotifyProvider';  // ← правильный импорт

const LANGS = [
  { value: 'cpp', label: 'C++' },
  { value: 'csharp', label: 'C#' },
  { value: 'python', label: 'Python' },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const navigate = useNavigate();
  const notify = useNotify(); // ← получаем notify из провайдера

  const [assignment, setAssignment] = useState(null);
  const [loading, setLoading] = useState(true);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const [error, setError] = useState('');
  const [errorDetail, setErrorDetail] = useState('');
  const [result, setResult] = useState(null);
  const [lastSmoke, setLastSmoke] = useState(null);

  useEffect(() => {
    let ok = true;
    (async () => {
      try {
        const data = await getAssignment(assignmentId);
        if (!ok) return;
        setAssignment(data);
        if (data?.language) setLanguage(data.language);
      } catch {
        notify.error('Не удалось загрузить задание');
      } finally {
        if (ok) setLoading(false);
      }
    })();
    return () => (ok = false);
  }, [assignmentId, notify]);

  const publicTests = useMemo(
    () => (assignment?.testCases || []).filter((t) => !t.isHidden),
    [assignment]
  );

  const handleError = (kind, detail) => {
    setError(kind || 'error');
    setErrorDetail(detail || '');
    notify.error(kind === 'compile_error' ? 'Ошибка компиляции' : 'Ошибка выполнения');
  };

  const openDetails = (payload) => {
    navigate(`/assignment/${assignmentId}/results`, { state: payload });
  };

  const onSubmit = async () => {
    setSubmitting(true);
    setError('');
    setErrorDetail('');
    setResult(null);
    setLastSmoke(null);

    // SMOKE: вместо пустого ввода прогоняем один публичный тест
    try {
      const smokeTests = publicTests.length ? [publicTests[0]] : [];
      if (smokeTests.length) {
        const resp = await runCompilerTests({ language, code, testCases: smokeTests });
        setLastSmoke(resp);
        const first = (resp?.results || [])[0];
        if (first && !first.passed) {
          const status = first.status || (first.exitCode !== 0 ? 'runtime_error' : 'ok');
          const detail = first.compileStderr || first.stderr || '';
          handleError(status === 'ok' ? 'runtime_error' : status, detail);
          setSubmitting(false);
          return;
        }
      }
    } catch (smokeErr) {
      const data = smokeErr?.response?.data || {};
      const detail =
        data?.compile?.stderr ||
        data?.compile?.stdout ||
        data?.message ||
        (typeof smokeErr?.message === 'string' ? smokeErr.message : '');
      handleError('compile_error', detail);
      setSubmitting(false);
      return;
    }

    // Смок прошёл — отправляем полноценную проверку
    try {
      const r = await submitSolution(assignmentId, { language, code });
      setResult(r);
      if (r.passedAll || r.passedAllTests) notify.success('Решение успешно: все тесты пройдены');
      else notify.info('Решение отправлено. Проверьте результаты тестов.');
    } catch (e) {
      setError(e.message || 'Не удалось отправить решение');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }

  if (!assignment) {
    return (
      <Layout>
        <div className="text-red-500">Задание не найдено</div>
      </Layout>
    );
  }

  return (
    <Layout>
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <Link to={`/assignment/${assignment.id}`} className="btn">
            ← Назад к заданию
          </Link>
          <div className="text-slate-500 text-sm">
            Публичных тестов: {publicTests.length} / скрытых: {assignment.hiddenTestCount ?? 0}
          </div>
        </div>

        <div className="flex items-center gap-2">
          <select
            className="select"
            value={language}
            onChange={(e) => setLanguage(e.target.value)}
            disabled={submitting}
          >
            {LANGS.map((l) => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </select>
          <Link to={`/assignment/${assignment.id}/top`} className="btn-outline">
            Топ решений
          </Link>
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{assignment.title}</h1>
            {assignment.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {assignment.tags
                  .split(',')
                  .filter(Boolean)
                  .map((t) => (
                    <span key={t} className="badge">
                      {t}
                    </span>
                  ))}
              </div>
            )}
            <div
              className="prose max-w-none"
              dangerouslySetInnerHTML={{
                __html: assignment.descriptionHtml || assignment.description,
              }}
            />
          </Card>

          <Card>
            <div className="mb-3 text-slate-600 text-sm">Редактор кода</div>
            <CodeEditor
              language={language}
              value={code}
              onChange={setCode}
              height={420}
              readOnly={submitting}
            />

            <div className="mt-4 flex gap-3">
              <button className="btn" onClick={onSubmit} disabled={submitting}>
                {submitting ? 'Проверяем…' : 'Отправить'}
              </button>

              {lastSmoke && (
                <button
                  className="btn-outline"
                  onClick={() => openDetails({ smoke: lastSmoke, assignmentId, language })}
                  disabled={submitting}
                >
                  Подробно (смок)
                </button>
              )}
              {result && (
                <button
                  className="btn-outline"
                  onClick={() => openDetails({ result, assignmentId, language })}
                  disabled={submitting}
                >
                  Подробный отчёт
                </button>
              )}
            </div>
          </Card>

          {error && (
            <Card tone="danger">
              <div className="font-semibold mb-1">
                {error === 'compile_error' ? 'Ошибка компиляции' : 'Ошибка выполнения'}
              </div>
              {errorDetail && (
                <pre className="text-sm overflow-x-auto whitespace-pre-wrap">{errorDetail}</pre>
              )}
              {lastSmoke && (
                <div className="mt-2">
                  <button
                    className="btn-outline"
                    onClick={() => openDetails({ smoke: lastSmoke, assignmentId, language })}
                  >
                    Открыть подробности смока
                  </button>
                </div>
              )}
            </Card>
          )}

          {result && (
            <Card>
              <div className="font-semibold mb-2">Результат проверки</div>
              <div className="text-sm">
                Пройдено: {result.passed} • Провалено: {result.failed}
              </div>
              <div className="mt-3">
                <button
                  className="btn-outline"
                  onClick={() => openDetails({ result, assignmentId, language })}
                >
                  Подробный отчёт
                </button>
              </div>
            </Card>
          )}
        </div>

        <div className="space-y-5">
          <Card>
            <div className="font-semibold mb-2">Публичные тесты</div>
            {publicTests.length === 0 && (
              <div className="text-slate-500 text-sm">Нет публичных тестов</div>
            )}
            {publicTests.length > 0 && (
              <ul className="space-y-2 text-sm">
                {publicTests.map((t, i) => (
                  <li key={i} className="rounded border p-2 bg-slate-50">
                    <div className="text-slate-500">Ввод:</div>
                    <pre className="whitespace-pre-wrap">{t.input || '⟨пусто⟩'}</pre>
                    <div className="text-slate-500 mt-2">Ожидаемый вывод:</div>
                    <pre className="whitespace-pre-wrap">{t.expectedOutput || '⟨пусто⟩'}</pre>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      </div>
    </Layout>
  );
}
