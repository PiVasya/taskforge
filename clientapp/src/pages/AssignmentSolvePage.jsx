// src/pages/AssignmentSolvePage.jsx
import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Select, Textarea, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';

import { useNotify } from '../components/notify/NotifyProvider'; // правильный импорт
import { getAssignment } from '../api/assignments';               // как и было
import { submitSolution } from '../api/solutions';                // теперь путь на /api/assignments/{id}/submit
import { runTests as runCompilerTests } from '../api/compiler';

import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';

const LANGS = [
  { value: 'cpp', label: 'C++' },
  { value: 'python', label: 'Python' },
  { value: 'csharp', label: 'C#' },
];

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const notify = useNotify();

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [plainMode, setPlainMode] = useState(false);

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState(null); // { passedAllTests, results: [...] }

  useEffect(() => {
    let alive = true;
    (async () => {
      setLoading(true);
      setError('');
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;
        setA(data);
        if (data?.defaultLanguage) setLanguage(data.defaultLanguage);
        if (data?.starterCode) setCode(data.starterCode);
      } catch (e) {
        const msg = e?.response?.data?.error || e?.message || 'Не удалось загрузить задание';
        if (alive) {
          setError(msg);
          notify.error(msg);
        }
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  const onSubmit = async () => {
    if (!code.trim()) return;
    setSubmitting(true);
    setError('');
    setResult(null);
    try {
      // SMOKE: прогоняем первый публичный тест (реальный ввод), чтобы поймать синтаксис/рантайм ДО полного прогона
      const firstPublic = (a?.testCases || []).find((t) => !t.isHidden);
      if (firstPublic) {
        try {
          const smoke = await runCompilerTests({ language, code, testCases: [firstPublic] });
          const scase = (smoke?.results || smoke?.testCases || smoke?.cases || [])[0] || {};
          const failed =
            Boolean(scase.error || scase.compileError || scase.stderr) ||
            scase.status === 'FAIL' || scase.passed === false || scase.ok === false;
          if (failed) {
            // сохраняем результат и открываем отдельную страницу результатов — код остаётся на месте
            try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: smoke })); } catch {}
            window.open(`/assignment/${assignmentId}/results?view=smoke`, '_blank', 'noopener,noreferrer');
            notify.error('Пробный прогон не прошёл. Детали — на странице результатов.');
            setSubmitting(false);
            return;
          }
        } catch (smokeErr) {
          const msg = smokeErr?.response?.data?.error || smokeErr?.message || 'Ошибка пробного прогона';
          const payload = { results: [{ compileStderr: msg }] };
          try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: payload })); } catch {}
          window.open(`/assignment/${assignmentId}/results?view=smoke`, '_blank', 'noopener,noreferrer');
          notify.error(msg);
          setSubmitting(false);
          return;
        }
      }

      // Если SMOKE прошёл — сабмитим и запускаем все тесты
      const r = await submitSolution(assignmentId, { language, code });
      setResult(r);

      // уведомления + отдельная страница результатов
      if (r?.passedAllTests || r?.passedAll) {
        notify.success('Все тесты пройдены!');
      } else if (r?.compileError) {
        notify.error('Ошибка компиляции');
      } else {
        notify.error('Не все тесты пройдены');
      }
      try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: r })); } catch {}
      window.open(`/assignment/${assignmentId}/results`, '_blank', 'noopener,noreferrer');
    } catch (e) {
      const msg = e?.response?.data?.error || e?.message || 'Не удалось отправить решение';
      setError(msg);
      notify.error(msg);
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
  if (!a) {
    return (
      <Layout>
        <div className="text-red-600">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  const publicTests = (a.testCases || []).filter((t) => !t.isHidden);

  return (
    <Layout>
      {/* верхняя панель */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline flex items-center gap-1">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
          {/* Кнопку «Топ решений» убрали по твоему запросу */}
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* левая часть: текст задачи + публичные тесты */}
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
            {a.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {a.tags
                  .split(',')
                  .filter(Boolean)
                  .map((t) => (
                    <Badge key={t.trim()}>{t.trim()}</Badge>
                  ))}
              </div>
            )}
            <div className="prose max-w-none whitespace-pre-wrap break-words">{a.description}</div>
          </Card>

          <Card>
            <div className="flex items-center justify-between mb-3">
              <div className="font-medium">Публичные тесты</div>
            </div>

            {publicTests.length === 0 ? (
              <div className="text-slate-500">У задания нет публичных тестов.</div>
            ) : (
              <div className="space-y-3">
                {publicTests.map((t, i) => (
                  <div key={i} className="rounded border p-3">
                    <div className="text-xs text-slate-500 mb-1">Вход</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.input ?? ''}</pre>
                    {'expected' in t && (
                      <>
                        <div className="text-xs text-slate-500 mt-2 mb-1">Ожидаемый вывод</div>
                        <pre className="whitespace-pre-wrap text-sm">{t.expected ?? ''}</pre>
                      </>
                    )}
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        {/* правая часть: редактор и запуск */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {LANGS.map((l) => (
                    <option key={l.value} value={l.value}>{l.label}</option>
                  ))}
                </Select>
              </div>

              <div>
                <label className="label">Режим ввода</label>
                <Select
                  value={plainMode ? 'plain' : 'editor'}
                  onChange={(e) => setPlainMode(e.target.value === 'plain')}
                >
                  <option value="editor">Графический редактор</option>
                  <option value="plain">Простой текст</option>
                </Select>
              </div>

              <div>
                <label className="label">Ваш код</label>
                {plainMode ? (
                  <Textarea value={code} onChange={(e) => setCode(e.target.value)} rows={16} />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} height={380} />
                )}
              </div>

              <div className="flex items-center gap-2">
                <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
                  <Play size={16} className="mr-1" />
                  {submitting ? 'Отправка…' : 'Отправить'}
                </Button>
                {result && (
                  <div className="flex items-center gap-2 text-sm">
                    {result.passedAllTests ? (
                      <>
                        <CheckCircle2 className="text-emerald-600" size={16} /> Все тесты пройдены
                      </>
                    ) : (
                      <>
                        <XCircle className="text-red-600" size={16} /> Не все тесты пройдены
                      </>
                    )}
                  </div>
                )}
              </div>

              {error && (
                <div className="text-sm text-red-600">
                  {error}
                </div>
              )}
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
