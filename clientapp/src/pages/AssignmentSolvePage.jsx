import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Textarea, Select, Badge } from '../components/ui';
import CodeEditor from '../components/CodeEditor';
import IfEditor from '../components/IfEditor';
import { getAssignment } from '../api/assignments';
import { submitSolution } from '../api/solutions';
import { ArrowLeft, Play } from 'lucide-react';
import { useNotify } from '../components/notify/useNotify';

const LANGS = [
  { value: 'cpp', label: 'C++' },
  { value: 'csharp', label: 'C#' },
  { value: 'python', label: 'Python' },
];

// утилита для хранения кода между переходами
const codeKey = (assignmentId, language) => `solve:${assignmentId}:code:${language}`;

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const notify = useNotify();

  const [loading, setLoading] = useState(true);
  const [a, setA] = useState(null);
  const [error, setError] = useState('');

  const [language, setLanguage] = useState('cpp');
  const [plainMode, setPlainMode] = useState(false);
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);

  // загрузка задания
  useEffect(() => {
    let ignore = false;
    (async () => {
      try {
        setLoading(true);
        const data = await getAssignment(assignmentId);
        if (ignore) return;
        setA(data);
        // если ранее сохраняли код для этого задания/языка — подставим
        const saved = sessionStorage.getItem(codeKey(assignmentId, language));
        if (saved != null) setCode(saved);
      } catch (e) {
        setError(e?.message || 'Не удалось загрузить задание');
      } finally {
        setLoading(false);
      }
    })();
    return () => { ignore = true; };
  }, [assignmentId]);

  // подмена кода при смене языка с учётом сохранённого
  useEffect(() => {
    const saved = sessionStorage.getItem(codeKey(assignmentId, language));
    if (saved != null) setCode(saved);
  }, [assignmentId, language]);

  // сохраняем код на лету
  useEffect(() => {
    sessionStorage.setItem(codeKey(assignmentId, language), code ?? '');
  }, [assignmentId, language, code]);

  const publicTests = useMemo(
    () => (a?.testCases || []).filter((t) => !t.isHidden),
    [a]
  );

  const onSubmit = async () => {
    if (!code.trim()) {
      notify.error('Введите код решения');
      return;
    }

    setSubmitting(true);
    setError('');
    try {
      const result = await submitSolution(assignmentId, { language, code });

      // положим результат в localStorage, чтобы отдельная страница могла его прочитать
      localStorage.setItem(
        `results:${assignmentId}`,
        JSON.stringify({ when: Date.now(), result })
      );

      // уведомления
      if (result?.passedAll || result?.passedAllTests) {
        notify.success('Все тесты пройдены!');
      } else {
        notify.error('Есть непройденные тесты');
      }

      // переход на страницу результатов (код в редакторе остаётся в sessionStorage)
      nav(`/assignment/${assignmentId}/results`);
    } catch (e) {
      const msg = e?.message || 'Не удалось отправить решение';
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
        <div className="text-red-500">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  return (
    <Layout>
      {/* верхняя панель: назад к ЗАДАНИЯМ и (для редактора) ссылка на правку */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
          {/* Кнопку «Топ решений» убираем, как просил */}
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* левая часть: описание и публичные тесты */}
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
            <div className="prose prose-slate dark:prose-invert max-w-none">
              {a.description ? (
                <pre className="whitespace-pre-wrap">{a.description}</pre>
              ) : (
                <p className="text-slate-500">Описание не задано.</p>
              )}
            </div>
          </Card>

          <Card>
            <h2 className="text-lg font-semibold mb-3">Публичные тесты</h2>
            {publicTests.length === 0 ? (
              <div className="text-slate-500">Публичных тестов нет.</div>
            ) : (
              <div className="grid sm:grid-cols-2 gap-4">
                {publicTests.map((t, i) => (
                  <div
                    key={t.id ?? i}
                    className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40"
                  >
                    <div className="text-xs text-slate-500 mb-1">Ввод</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.input}</pre>
                    <div className="text-xs text-slate-500 mt-2 mb-1">Ожидаемый вывод</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.expectedOutput}</pre>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        {/* правая колонка: выбор языка, режим ввода, редактор и кнопка Отправить */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {LANGS.map((l) => (
                    <option key={l.value} value={l.value}>
                      {l.label}
                    </option>
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
                  <Textarea
                    rows={14}
                    value={code}
                    onChange={(e) => setCode(e.target.value)}
                    placeholder="// Напишите решение..."
                  />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} />
                )}
              </div>

              <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
                <Play size={16} /> {submitting ? 'Отправляю…' : 'Отправить'}
              </Button>
              {error && <div className="text-red-500 text-sm">{error}</div>}
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
