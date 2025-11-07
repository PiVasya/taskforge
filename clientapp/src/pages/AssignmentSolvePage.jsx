import React, { useEffect, useMemo, useState, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';
import CodeEditor from '../components/CodeEditor';
import { useNotify } from '../components/notify/NotifyProvider';

import { getAssignment } from '../api/assignments';
import { runTests as runCompilerTests } from '../api/compiler';

const LANGS = [
  { value: 'cpp', label: 'C++' },
  { value: 'csharp', label: 'C#' },
  { value: 'python', label: 'Python' },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const navigate = useNavigate();
  const notify = useNotify();

  const [loading, setLoading] = useState(true);
  const [assignment, setAssignment] = useState(null);

  const [language, setLanguage] = useState('cpp');
  const [source, setSource] = useState('');
  const [isRunning, setIsRunning] = useState(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;
        setAssignment(data);
        if (data?.defaultLanguage && LANGS.some(l => l.value === data.defaultLanguage)) {
          setLanguage(data.defaultLanguage);
        }
      } catch {
        notify.error('Не удалось загрузить задание');
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId, notify]);

  const publicTests = useMemo(() => {
    return (assignment?.testCases || []).filter(t => !t.isHidden);
  }, [assignment]);

  const goBackToAssignments = useCallback(() => {
    const courseId = assignment?.courseId ?? assignment?.course?.id;
    if (courseId) navigate(`/course/${courseId}`);
    else navigate('/courses');
  }, [assignment, navigate]);

  const runAllTests = useCallback(async () => {
    if (!assignment) return;
    if (!source.trim()) {
      notify.error('Код пустой');
      return;
    }

    setIsRunning(true);
    try {
      const payload = {
        language,
        code: source,
        testCases: assignment.testCases?.map(t => ({
          input: t.input ?? '',
          expectedOutput: t.expectedOutput ?? '',
        })) ?? [],
      };

      const result = await runCompilerTests(payload);

      if (result?.status === 'passed' || result?.passedAll) {
        notify.success('Все тесты пройдены!');
      } else if (result?.compile?.error) {
        notify.error('Ошибка компиляции. Открой подробности.');
      } else if (result?.run?.error) {
        notify.error('Ошибка выполнения. Открой подробности.');
      } else {
        notify.error('Есть непройденные тесты.');
      }
    } catch (e) {
      console.error(e);
      notify.error('Не удалось выполнить тесты');
    } finally {
      setIsRunning(false);
    }
  }, [assignment, language, source, notify]);

  if (loading) {
    return (
      <Layout>
        <div className="container-app py-10">Загрузка…</div>
      </Layout>
    );
  }

  if (!assignment) {
    return (
      <Layout>
        <div className="container-app py-10">Задание не найдено</div>
      </Layout>
    );
  }

  return (
    <Layout>
      <div className="container-app py-6 space-y-6">
        {/* Назад */}
        <button
          type="button"
          onClick={goBackToAssignments}
          className="text-sm text-slate-600 hover:text-slate-800"
        >
          ← К заданиям
        </button>

        <h1 className="text-2xl font-semibold">{assignment.title || 'Задание'}</h1>

        {/* Описание */}
        <Card>
          <div className="prose prose-slate max-w-none whitespace-pre-wrap leading-relaxed">
            {assignment.description}
          </div>
        </Card>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Редактор */}
          <Card className="lg:col-span-2">
            <div className="flex items-center gap-3 mb-3">
              <label className="text-sm text-slate-600">Язык</label>
              <select
                value={language}
                onChange={e => setLanguage(e.target.value)}
                className="border rounded px-2 py-1"
              >
                {LANGS.map(l => <option key={l.value} value={l.value}>{l.label}</option>)}
              </select>
            </div>

            <CodeEditor
              language={language}
              value={source}
              onChange={setSource}
              minHeight={360}
            />

            <div className="mt-4">
              <button
                type="button"
                onClick={runAllTests}
                disabled={isRunning}
                className="btn btn-primary"
              >
                {isRunning ? 'Запускаю…' : 'Отправить'}
              </button>
            </div>
          </Card>

          {/* Публичные тесты */}
          <Card>
            <div className="font-medium mb-2">Публичные тесты</div>
            {publicTests.length === 0 ? (
              <div className="text-slate-500">Нет публичных тестов</div>
            ) : (
              <ul className="space-y-4 text-sm">
                {publicTests.map((t, i) => (
                  <li key={i}>
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
