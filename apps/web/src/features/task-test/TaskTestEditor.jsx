import React, { useState } from 'react';
import { Button, Card, Field, Input, Select, Textarea, Badge } from '../../components/ui';
import StatementEditor from '../../components/tiptap/StatementEditor';
import { QuizQuestionView, QuizChoiceView } from './QuizRichView';
import { hasQuizContent, quizDocumentPlainText, quizRichValue } from './quizRichContent';

const QUESTION_TYPES = [
  { value: 'single-choice', label: 'A/B/C/D (один вариант)' },
  { value: 'multi-choice', label: 'A/B/C/D (несколько вариантов)' },
  { value: 'fill', label: 'Вставить пропущенное слово' },
  { value: 'text', label: 'Текстовый ответ' },
];

function normalizeLimits(x) {
  if (!Array.isArray(x)) return [];
  return x.map(v => (v == null || v === '' ? null : Number(v)))
    .map(v => (Number.isFinite(v) && v > 0 ? Math.floor(v) : null));
}

export default function TaskTestEditor({ settings, setSettings, questions, setQuestions }) {
  const s = settings || {
    maxAttempts: 1,
    unlimitedAttempts: false,
    passPercent: 60,
    shuffleQuestions: true,
    shuffleAnswers: true,
    allowReview: true,
    attemptTimeLimitsSeconds: [],
  };

  const qList = Array.isArray(questions) ? questions : [];
  const [activeEditorKey, setActiveEditorKey] = useState(null);

  const updateOption = (idx, oi, patch) => {
    setQuestions((current) => current.map((question, questionIndex) => {
      if (questionIndex !== idx) return question;
      return { ...question, options: (question.options || []).map((option, optionIndex) =>
        optionIndex === oi ? { ...option, ...patch } : option) };
    }));
  };

  const activateEditor = (key) => setActiveEditorKey(key === activeEditorKey ? null : key);

  const renderModeButtons = (mode, activate) => (
    <div className="inline-flex items-center gap-1" role="group" aria-label="Режим редактирования">
      <Button variant={mode === 'plain' ? 'primary' : 'outline'} onClick={() => activate('plain')}>TXT</Button>
      <Button variant={mode === 'rich' ? 'primary' : 'outline'} onClick={() => activate('rich')}>Tiptap</Button>
    </div>
  );

  const updateSettings = (patch) => {
    setSettings({ ...s, ...patch, attemptTimeLimitsSeconds: normalizeLimits(patch.attemptTimeLimitsSeconds ?? s.attemptTimeLimitsSeconds) });
  };

  const addQuestion = () => {
    
    const nextOrder = qList.length
      ? Math.max(...qList.map(x => (Number.isFinite(x.order) ? x.order : 0))) + 1
      : 0;
    setQuestions([
      ...qList,
      {
        id: '00000000-0000-0000-0000-000000000000',
        order: nextOrder,
        type: 'single-choice',
        prompt: '',
        promptContentJson: quizRichValue('', ''),
        promptEditorMode: 'plain',
        optionsLayout: 'list',
        options: [
          { key: 'a', text: '', contentJson: '', editorMode: 'plain' },
          { key: 'b', text: '', contentJson: '', editorMode: 'plain' },
          { key: 'c', text: '', contentJson: '', editorMode: 'plain' },
          { key: 'd', text: '', contentJson: '', editorMode: 'plain' },
        ],
        correctOptionKeys: ['a'],
        acceptedAnswers: [],
        caseSensitive: false,
        trim: true,
      },
    ]);
    setActiveEditorKey(`question:${qList.length}`);
  };

  const removeQuestion = (idx) => {
    setActiveEditorKey(null);
    setQuestions((current) => current.filter((_, i) => i !== idx));
  };

  const moveQuestion = (idx, dir) => {
    const j = idx + dir;
    if (j < 0 || j >= qList.length) return;
    const copy = [...qList];
    const t = copy[idx];
    copy[idx] = copy[j];
    copy[j] = t;
    
    
    const withOrder = copy.map((x, i) => ({ ...x, order: i }));
    setActiveEditorKey(null);
    setQuestions(withOrder);
  };

  const updateQuestion = (idx, patch) => {
    setQuestions((current) => current.map((question, i) => i === idx ? { ...question, ...patch } : question));
  };

  const renderQuestionBody = (q, idx) => {
    const type = q.type || 'single-choice';

    if (type === 'single-choice' || type === 'multi-choice') {
      const isMulti = type === 'multi-choice';
      const opts = Array.isArray(q.options) ? q.options : [];
      const correct = new Set(Array.isArray(q.correctOptionKeys) ? q.correctOptionKeys : []);

      return (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-sm text-neutral-600 dark:text-neutral-300">
              Варианты ответа ({isMulti ? 'пометь правильные' : 'пометь правильный'})
            </span>
            <label className="flex items-center gap-2 text-sm">
              Расположение
              <Select value={q.optionsLayout === 'inline' ? 'inline' : 'list'} onChange={(e) => updateQuestion(idx, { optionsLayout: e.target.value })}>
                <option value="list">Столбиком</option>
                <option value="inline">В строку</option>
              </Select>
            </label>
          </div>
          <div className="space-y-3">
            {opts.map((o, oi) => {
              const optionKey = `option:${idx}:${oi}`;
              const active = activeEditorKey === optionKey;
              const mode = o.editorMode === 'rich' ? 'rich' : 'plain';
              return <div key={o.key || oi} className="rounded-xl border border-[rgb(var(--border))] p-3 space-y-2">
                <div className="flex flex-wrap items-center gap-2">
                  <input
                    type={isMulti ? 'checkbox' : 'radio'}
                    name={`q_${idx}_correct`}
                    checked={correct.has(o.key)}
                    aria-label={`Правильный вариант ${o.key}`}
                    onChange={() => {
                      if (!isMulti) return updateQuestion(idx, { correctOptionKeys: [o.key] });
                      const next = new Set(correct);
                      if (next.has(o.key) && next.size === 1) return;
                      if (next.has(o.key)) next.delete(o.key);
                      else next.add(o.key);
                      updateQuestion(idx, { correctOptionKeys: Array.from(next) });
                    }}
                  />
                  <span className="text-sm font-medium">Вариант {o.key}</span>
                  <div className="ml-auto flex flex-wrap items-center gap-2">
                    {renderModeButtons(mode, (nextMode) => {
                      updateOption(idx, oi, { editorMode: nextMode });
                      setActiveEditorKey(optionKey);
                    })}
                    <Button variant="outline" onClick={() => activateEditor(optionKey)}>
                      {active ? 'Свернуть' : 'Редактировать'}
                    </Button>
                    <Button variant="outline" onClick={() => {
                      const next = opts.filter((_, index) => index !== oi);
                      const nextCorrect = (q.correctOptionKeys || []).filter((key) => key !== o.key);
                      updateQuestion(idx, { options: next, correctOptionKeys: nextCorrect.length ? nextCorrect : (next[0] ? [next[0].key] : []) });
                      setActiveEditorKey(null);
                    }}>Удалить</Button>
                  </div>
                </div>
                {active ? (
                  <StatementEditor
                    key={optionKey}
                    value={quizRichValue(o.contentJson, o.text)}
                    mode={mode}
                    compact
                    preserveLiteral
                    onChange={(next) => updateOption(idx, oi, {
                      contentJson: next,
                      text: quizDocumentPlainText(next),
                    })}
                  />
                ) : (
                  <div className="cursor-text min-h-6" onClick={() => setActiveEditorKey(optionKey)}>
                    {hasQuizContent(o.contentJson, o.text) ? <QuizChoiceView option={o} /> : <span className="text-sm text-neutral-500">Нажми, чтобы заполнить вариант</span>}
                  </div>
                )}
              </div>;
            })}
          </div>
          <Button
            variant="outline"
            onClick={() => {
              const used = new Set(opts.map((opt) => opt.key));
              const nextKey = Array.from('abcdefghijklmnopqrstuvwxyz').find((key) => !used.has(key)) || `v${opts.length + 1}`;
              updateQuestion(idx, { options: [...opts, { key: nextKey, text: '', contentJson: '', editorMode: 'plain' }] });
            }}
          >
            + Добавить вариант
          </Button>
        </div>
      );
    }

    
    const answers = Array.isArray(q.acceptedAnswers) ? q.acceptedAnswers : [];
    return (
      <div className="space-y-3">
        <div className="text-sm text-neutral-600">
          Допустимые ответы (по одному на строку)
        </div>
        <Textarea
          rows={4}
          value={answers.join('\n')}
          
          
          onChange={(e) => updateQuestion(idx, { acceptedAnswers: e.target.value.split(/\r?\n/).map((x) => x) })}
          placeholder={type === 'fill' ? 'Напр.: apple\nApple' : 'Напр.: 42'}
        />
        <div className="flex items-center gap-4">
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={!!q.trim}
              onChange={(e) => updateQuestion(idx, { trim: e.target.checked })}
            />
            Trim
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={!!q.caseSensitive}
              onChange={(e) => updateQuestion(idx, { caseSensitive: e.target.checked })}
            />
            Case sensitive
          </label>
        </div>
      </div>
    );
  };

  return (
    <div className="space-y-6">
      <Card>
        <h2 className="text-xl font-semibold">Настройки теста</h2>

        <div className="grid md:grid-cols-2 gap-4 mt-4">
          <Field label="Кол-во попыток">
            <div className="space-y-2">
              <Input
                type="number"
                min={1}
                value={s.maxAttempts ?? 1}
                disabled={!!s.unlimitedAttempts}
                onChange={(e) => updateSettings({ maxAttempts: Math.max(1, Number(e.target.value || 1)) })}
              />
              <label className="flex items-center gap-2 text-sm">
                <input
                  data-taskforge-automation-id="test-unlimited-attempts"
                  type="checkbox"
                  checked={!!s.unlimitedAttempts}
                  onChange={(e) => updateSettings({ unlimitedAttempts: e.target.checked })}
                />
                Бесконечные попытки
              </label>
            </div>
          </Field>
          <Field label="Проходной процент">
            <Input
              type="number"
              min={0}
              max={100}
              value={s.passPercent ?? 60}
              onChange={(e) => updateSettings({ passPercent: Math.max(0, Math.min(100, Number(e.target.value || 0))) })}
            />
          </Field>
        </div>

        <div className="flex flex-wrap items-center gap-6 mt-4">
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={!!s.shuffleQuestions}
              onChange={(e) => updateSettings({ shuffleQuestions: e.target.checked })}
            />
            Случайный порядок вопросов
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={!!s.shuffleAnswers}
              onChange={(e) => updateSettings({ shuffleAnswers: e.target.checked })}
            />
            Случайный порядок ответов
          </label>

          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={s.allowReview !== false}
              onChange={(e) => updateSettings({ allowReview: e.target.checked })}
            />
            Разрешить просмотр результатов (мои ответы)
          </label>
        </div>

        <div className="mt-4">
          <div className="flex items-center justify-between">
            <div className="font-medium">Таймер (сек) по попыткам</div>
            <Button
              variant="outline"
              onClick={() => updateSettings({ attemptTimeLimitsSeconds: [...(s.attemptTimeLimitsSeconds || []), null] })}
            >
              + Добавить попытку
            </Button>
          </div>
          <div className="grid md:grid-cols-2 gap-3 mt-3">
            {(s.attemptTimeLimitsSeconds || []).map((v, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <Badge>Попытка {idx + 1}</Badge>
                <Input
                  type="number"
                  min={1}
                  placeholder="без таймера"
                  value={v == null ? '' : v}
                  onChange={(e) => {
                    const copy = [...(s.attemptTimeLimitsSeconds || [])];
                    const num = Number(e.target.value);
                    copy[idx] = Number.isFinite(num) && num > 0 ? Math.floor(num) : null;
                    updateSettings({ attemptTimeLimitsSeconds: copy });
                  }}
                />
                <Button
                  variant="outline"
                  onClick={() => {
                    const copy = [...(s.attemptTimeLimitsSeconds || [])];
                    copy.splice(idx, 1);
                    updateSettings({ attemptTimeLimitsSeconds: copy });
                  }}
                >
                  Удалить
                </Button>
              </div>
            ))}
          </div>
          <div className="text-xs text-neutral-500 mt-2">Пусто = без таймера.</div>
        </div>
      </Card>

      <Card>
        <div className="flex items-center justify-between">
          <h2 className="text-xl font-semibold">Вопросы</h2>
          <Button onClick={addQuestion}>+ Добавить вопрос</Button>
        </div>

        {qList.length === 0 ? (
          <div className="text-neutral-500 mt-3">Вопросов пока нет</div>
        ) : (
          <div className="space-y-4 mt-4">
            {qList.map((q, idx) => (
              <div key={idx} className="border rounded-xl p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex items-center gap-2">
                    <Badge>#{idx + 1}</Badge>
                    <div className="text-sm text-neutral-600">ID: {String(q.id || '').slice(0, 8)}…</div>
                  </div>
                  <div className="flex items-center gap-2">
                    <Button variant="outline" onClick={() => moveQuestion(idx, -1)}>↑</Button>
                    <Button variant="outline" onClick={() => moveQuestion(idx, 1)}>↓</Button>
                    <Button variant="outline" onClick={() => removeQuestion(idx)}>Удалить</Button>
                  </div>
                </div>

                <div className="grid md:grid-cols-3 gap-3 mt-3">
                  <Field label="Тип">
                    <Select
                      value={q.type || 'single-choice'}
                      onChange={(e) => {
                        const t = e.target.value;
                        const patch = { type: t };
                        const isChoice = t === 'single-choice' || t === 'multi-choice';
                        if (isChoice && (!q.options || q.options.length === 0)) {
                          patch.options = [
                            { key: 'a', text: '' },
                            { key: 'b', text: '' },
                            { key: 'c', text: '' },
                            { key: 'd', text: '' },
                          ];
                          patch.correctOptionKeys = ['a'];
                        }
                        
                        if (t === 'single-choice') {
                          const cur = Array.isArray(q.correctOptionKeys) ? q.correctOptionKeys : [];
                          const one = cur[0] || 'a';
                          patch.correctOptionKeys = [one];
                        }
                        updateQuestion(idx, patch);
                      }}
                    >
                      {QUESTION_TYPES.map(x => (
                        <option key={x.value} value={x.value}>{x.label}</option>
                      ))}
                    </Select>
                  </Field>
                  <Field label="Порядок">
                    <Input
                      type="number"
                      value={q.order ?? (idx + 1)}
                      onChange={(e) => updateQuestion(idx, { order: Math.max(1, Number(e.target.value || 1)) })}
                    />
                  </Field>
                  <div />
                </div>

                <div className="mt-3">
                  <Field label="Вопрос">
                    <div className="space-y-2">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        {renderModeButtons(q.promptEditorMode === 'rich' ? 'rich' : 'plain', (nextMode) => {
                          updateQuestion(idx, { promptEditorMode: nextMode });
                          setActiveEditorKey(`question:${idx}`);
                        })}
                        <Button variant="outline" onClick={() => activateEditor(`question:${idx}`)}>
                          {activeEditorKey === `question:${idx}` ? 'Свернуть' : 'Редактировать'}
                        </Button>
                      </div>
                      {activeEditorKey === `question:${idx}` ? (
                        <StatementEditor
                          key={`question:${idx}`}
                          value={quizRichValue(q.promptContentJson, q.prompt)}
                          mode={q.promptEditorMode === 'rich' ? 'rich' : 'plain'}
                          compact
                          preserveLiteral
                          onChange={(next) => updateQuestion(idx, {
                            promptContentJson: next,
                            prompt: quizDocumentPlainText(next),
                          })}
                        />
                      ) : (
                        <div className="cursor-text rounded-lg border border-[rgb(var(--border))] px-3 py-2 min-h-[72px]" onClick={() => setActiveEditorKey(`question:${idx}`)}>
                          {hasQuizContent(q.promptContentJson, q.prompt) ? <QuizQuestionView question={q} /> : <span className="text-neutral-500">Нажми, чтобы написать условие вопроса</span>}
                        </div>
                      )}
                    </div>
                  </Field>
                </div>

                <div className="mt-4">
                  {renderQuestionBody(q, idx)}
                </div>
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  );
}
