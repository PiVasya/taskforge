import React from 'react';
import { Button, Card, Field, Input, Select, Textarea, Badge } from '../components/ui';

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
        options: [
          { key: 'a', text: '' },
          { key: 'b', text: '' },
          { key: 'c', text: '' },
          { key: 'd', text: '' },
        ],
        correctOptionKeys: ['a'],
        acceptedAnswers: [],
        caseSensitive: false,
        trim: true,
      },
    ]);
  };

  const removeQuestion = (idx) => {
    const copy = [...qList];
    copy.splice(idx, 1);
    setQuestions(copy);
  };

  const moveQuestion = (idx, dir) => {
    const j = idx + dir;
    if (j < 0 || j >= qList.length) return;
    const copy = [...qList];
    const t = copy[idx];
    copy[idx] = copy[j];
    copy[j] = t;
    
    
    const withOrder = copy.map((x, i) => ({ ...x, order: i }));
    setQuestions(withOrder);
  };

  const updateQuestion = (idx, patch) => {
    const copy = [...qList];
    copy[idx] = { ...copy[idx], ...patch };
    setQuestions(copy);
  };

  const renderQuestionBody = (q, idx) => {
    const type = q.type || 'single-choice';

    if (type === 'single-choice' || type === 'multi-choice') {
      const isMulti = type === 'multi-choice';
      const opts = Array.isArray(q.options) ? q.options : [];
      const correct = new Set(Array.isArray(q.correctOptionKeys) ? q.correctOptionKeys : []);

      return (
        <div className="space-y-3">
          <div className="text-sm text-neutral-600">
            Варианты ответа ({isMulti ? 'пометь правильные' : 'пометь правильный'})
          </div>
          <div className="space-y-2">
            {opts.map((o, oi) => (
              <div key={oi} className="flex items-center gap-2">
                <input
                  type={isMulti ? 'checkbox' : 'radio'}
                  name={`q_${idx}_correct`}
                  checked={correct.has(o.key)}
                  onChange={() => {
                    if (!isMulti) {
                      updateQuestion(idx, { correctOptionKeys: [o.key] });
                      return;
                    }
                    const next = new Set(correct);
                    const has = next.has(o.key);
                    
                    if (has && next.size === 1) return;
                    if (has) next.delete(o.key);
                    else next.add(o.key);
                    updateQuestion(idx, { correctOptionKeys: Array.from(next) });
                  }}
                />
                <Input
                  value={o.text || ''}
                  placeholder={`Вариант ${o.key}`}
                  onChange={(e) => {
                    const next = opts.map((x, k) => (k === oi ? { ...x, text: e.target.value } : x));
                    updateQuestion(idx, { options: next });
                  }}
                />
                <Button
                  variant="outline"
                  onClick={() => {
                    const next = [...opts];
                    next.splice(oi, 1);
                    const nextCorrect = (q.correctOptionKeys || []).filter((k) => k !== o.key);
                    updateQuestion(idx, { options: next, correctOptionKeys: nextCorrect.length ? nextCorrect : (next[0] ? [next[0].key] : []) });
                  }}
                >
                  Удалить
                </Button>
              </div>
            ))}
          </div>
          <Button
            variant="outline"
            onClick={() => {
              const nextKey = String.fromCharCode(97 + opts.length); 
              updateQuestion(idx, { options: [...opts, { key: nextKey, text: '' }] });
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
                    <Textarea
                      rows={3}
                      value={q.prompt || ''}
                      onChange={(e) => updateQuestion(idx, { prompt: e.target.value })}
                      placeholder="Текст вопроса..."
                    />
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
