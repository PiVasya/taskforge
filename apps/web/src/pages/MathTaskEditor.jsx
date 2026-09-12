import React from 'react';
import { Button, Card, Field, Input, Select, Textarea, Badge } from '../components/ui';
import StatementEditor from '../components/tiptap/StatementEditor';

const BLOCK_TYPES = [
  { value: 'info', label: 'Инфо-блок / шаг условия' },
  { value: 'number', label: 'Числовой ответ с допуском' },
  { value: 'expression', label: 'Формула / выражение' },
  { value: 'set', label: 'Множество / список без порядка' },
  { value: 'single-choice', label: 'Один правильный вариант' },
  { value: 'multi-choice', label: 'Несколько правильных вариантов' },
  { value: 'order', label: 'Расположить шаги по порядку' },
  { value: 'match', label: 'Сопоставление пар' },
];

function blankOptions() {
  return [
    { key: 'a', text: '' },
    { key: 'b', text: '' },
    { key: 'c', text: '' },
    { key: 'd', text: '' },
  ];
}

function blankBlock(order = 0) {
  return {
    id: '00000000-0000-0000-0000-000000000000',
    order,
    kind: 'info',
    prompt: '',
    promptContentJson: '',
    score: 0,
    isRequired: true,
    options: blankOptions(),
    correctOptionKeys: ['a'],
    acceptedAnswers: [],
    caseSensitive: false,
    trim: true,
    numericTolerance: 0,
    orderItems: ['', ''],
    matchLeftItems: [
      { key: 'l1', text: '' },
      { key: 'l2', text: '' },
    ],
    matchRightItems: [
      { key: 'r1', text: '' },
      { key: 'r2', text: '' },
    ],
    matchPairs: [
      { leftKey: 'l1', rightKey: 'r1' },
      { leftKey: 'l2', rightKey: 'r2' },
    ],
  };
}

function normalizeLimits(x) {
  if (!Array.isArray(x)) return [];
  return x.map(v => (v == null || v === '' ? null : Number(v)))
    .map(v => (Number.isFinite(v) && v > 0 ? Math.floor(v) : null));
}

export default function MathTaskEditor({ settings, setSettings, blocks, setBlocks }) {
  const s = settings || {
    maxAttempts: 1,
    unlimitedAttempts: false,
    passPercent: 60,
    shuffleBlocks: false,
    allowReview: true,
    attemptTimeLimitsSeconds: [],
  };

  const list = Array.isArray(blocks) ? blocks : [];

  const updateSettings = (patch) => {
    setSettings({ ...s, ...patch, attemptTimeLimitsSeconds: normalizeLimits(patch.attemptTimeLimitsSeconds ?? s.attemptTimeLimitsSeconds) });
  };

  const addBlock = (kind = 'info') => {
    const order = list.length ? Math.max(...list.map(x => Number.isFinite(x.order) ? x.order : 0)) + 1 : 0;
    const item = blankBlock(order);
    item.kind = kind;
    item.score = kind === 'info' ? 0 : 1;
    setBlocks([...list, item]);
  };

  const updateBlock = (idx, patch) => {
    const copy = [...list];
    copy[idx] = { ...copy[idx], ...patch };
    setBlocks(copy);
  };

  const removeBlock = (idx) => {
    const copy = [...list];
    copy.splice(idx, 1);
    setBlocks(copy.map((x, i) => ({ ...x, order: i })));
  };

  const moveBlock = (idx, dir) => {
    const j = idx + dir;
    if (j < 0 || j >= list.length) return;
    const copy = [...list];
    [copy[idx], copy[j]] = [copy[j], copy[idx]];
    setBlocks(copy.map((x, i) => ({ ...x, order: i })));
  };

  const renderChoiceEditor = (b, idx, isMulti) => {
    const opts = Array.isArray(b.options) ? b.options : [];
    const correct = new Set(Array.isArray(b.correctOptionKeys) ? b.correctOptionKeys : []);
    return (
      <div className="space-y-3">
        {opts.map((o, oi) => (
          <div key={oi} className="flex items-center gap-2">
            <input
              type={isMulti ? 'checkbox' : 'radio'}
              name={`math_${idx}_correct`}
              checked={correct.has(o.key)}
              onChange={() => {
                if (!isMulti) {
                  updateBlock(idx, { correctOptionKeys: [o.key] });
                  return;
                }
                const next = new Set(correct);
                next.has(o.key) ? next.delete(o.key) : next.add(o.key);
                updateBlock(idx, { correctOptionKeys: Array.from(next) });
              }}
            />
            <Input value={o.text || ''} placeholder={`Вариант ${o.key}`} onChange={(e) => {
              const next = opts.map((x, k) => k === oi ? { ...x, text: e.target.value } : x);
              updateBlock(idx, { options: next });
            }} />
            <Button variant="outline" onClick={() => {
              const next = [...opts];
              next.splice(oi, 1);
              updateBlock(idx, { options: next, correctOptionKeys: (b.correctOptionKeys || []).filter(x => x !== o.key) });
            }}>Удалить</Button>
          </div>
        ))}
        <Button variant="outline" onClick={() => {
          const nextKey = String.fromCharCode(97 + opts.length);
          updateBlock(idx, { options: [...opts, { key: nextKey, text: '' }] });
        }}>+ Добавить вариант</Button>
      </div>
    );
  };

  const renderTextEditor = (b, idx, numberMode = false) => (
    <div className="space-y-3">
      <Field label={numberMode ? 'Правильные числа (по одному на строку)' : 'Допустимые ответы (по одному на строку)'}>
        <Textarea
          rows={4}
          value={(b.acceptedAnswers || []).join('\n')}
          onChange={(e) => updateBlock(idx, { acceptedAnswers: e.target.value.split(/\r?\n/) })}
        />
      </Field>
      <div className="flex flex-wrap items-center gap-4">
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={!!b.trim} onChange={(e) => updateBlock(idx, { trim: e.target.checked })} />
          Trim
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={!!b.caseSensitive} onChange={(e) => updateBlock(idx, { caseSensitive: e.target.checked })} />
          Case sensitive
        </label>
        {numberMode && (
          <div className="min-w-[180px]">
            <Field label="Допуск">
              <Input type="number" step="0.0001" value={b.numericTolerance ?? 0} onChange={(e) => updateBlock(idx, { numericTolerance: Number(e.target.value || 0) })} />
            </Field>
          </div>
        )}
      </div>
    </div>
  );

  const renderOrderEditor = (b, idx) => {
    const items = Array.isArray(b.orderItems) ? b.orderItems : [];
    return (
      <div className="space-y-2">
        {items.map((it, ii) => (
          <div key={ii} className="flex items-center gap-2">
            <Badge>{ii + 1}</Badge>
            <Input value={it || ''} onChange={(e) => {
              const next = items.map((x, k) => k === ii ? e.target.value : x);
              updateBlock(idx, { orderItems: next });
            }} />
            <Button variant="outline" onClick={() => {
              const next = [...items];
              next.splice(ii, 1);
              updateBlock(idx, { orderItems: next });
            }}>Удалить</Button>
          </div>
        ))}
        <Button variant="outline" onClick={() => updateBlock(idx, { orderItems: [...items, ''] })}>+ Добавить шаг</Button>
      </div>
    );
  };

  const renderMatchEditor = (b, idx) => {
    const left = Array.isArray(b.matchLeftItems) ? b.matchLeftItems : [];
    const right = Array.isArray(b.matchRightItems) ? b.matchRightItems : [];
    const pairs = Array.isArray(b.matchPairs) ? b.matchPairs : [];

    const updatePairsAuto = (leftItems, rightItems) => {
      const nextPairs = leftItems.slice(0, Math.min(leftItems.length, rightItems.length)).map((x, i) => ({ leftKey: x.key, rightKey: rightItems[i]?.key || '' }));
      updateBlock(idx, { matchLeftItems: leftItems, matchRightItems: rightItems, matchPairs: nextPairs });
    };

    return (
      <div className="grid lg:grid-cols-3 gap-4">
        <div className="space-y-2">
          <div className="font-medium">Левая колонка</div>
          {left.map((it, i) => (
            <div key={i} className="flex items-center gap-2">
              <Input value={it.text || ''} onChange={(e) => {
                const next = left.map((x, k) => k === i ? { ...x, text: e.target.value } : x);
                updateBlock(idx, { matchLeftItems: next });
              }} />
            </div>
          ))}
          <Button variant="outline" onClick={() => {
            const next = [...left, { key: `l${left.length + 1}`, text: '' }];
            updatePairsAuto(next, right);
          }}>+ Левый элемент</Button>
        </div>
        <div className="space-y-2">
          <div className="font-medium">Правая колонка</div>
          {right.map((it, i) => (
            <div key={i} className="flex items-center gap-2">
              <Input value={it.text || ''} onChange={(e) => {
                const next = right.map((x, k) => k === i ? { ...x, text: e.target.value } : x);
                updateBlock(idx, { matchRightItems: next });
              }} />
            </div>
          ))}
          <Button variant="outline" onClick={() => {
            const next = [...right, { key: `r${right.length + 1}`, text: '' }];
            updatePairsAuto(left, next);
          }}>+ Правый элемент</Button>
        </div>
        <div className="space-y-2">
          <div className="font-medium">Правильные пары</div>
          {left.map((it, i) => (
            <div key={it.key || i} className="flex items-center gap-2">
              <Badge>{it.text || it.key}</Badge>
              <Select
                value={(pairs.find(p => p.leftKey === it.key)?.rightKey) || ''}
                onChange={(e) => {
                  const next = left.map((x) => ({ leftKey: x.key, rightKey: (pairs.find(p => p.leftKey === x.key)?.rightKey) || '' }));
                  const idxPair = next.findIndex(p => p.leftKey === it.key);
                  next[idxPair] = { leftKey: it.key, rightKey: e.target.value };
                  updateBlock(idx, { matchPairs: next });
                }}
              >
                <option value="">—</option>
                {right.map(r => <option key={r.key} value={r.key}>{r.text || r.key}</option>)}
              </Select>
            </div>
          ))}
        </div>
      </div>
    );
  };

  return (
    <div className="space-y-6">
      <Card>
        <h2 className="text-xl font-semibold">Math-builder: настройки</h2>
        <div className="grid md:grid-cols-2 gap-4 mt-4">
          <Field label="Кол-во попыток">
            <div className="space-y-2">
              <Input type="number" min={1} value={s.maxAttempts ?? 1} disabled={!!s.unlimitedAttempts} onChange={(e) => updateSettings({ maxAttempts: Math.max(1, Number(e.target.value || 1)) })} />
              <label className="flex items-center gap-2 text-sm">
                <input
                  data-taskforge-automation-id="math-unlimited-attempts"
                  type="checkbox"
                  checked={!!s.unlimitedAttempts}
                  onChange={(e) => updateSettings({ unlimitedAttempts: e.target.checked })}
                />
                Бесконечные попытки
              </label>
            </div>
          </Field>
          <Field label="Проходной процент">
            <Input type="number" min={0} max={100} value={s.passPercent ?? 60} onChange={(e) => updateSettings({ passPercent: Math.max(0, Math.min(100, Number(e.target.value || 0))) })} />
          </Field>
        </div>
        <div className="flex flex-wrap items-center gap-6 mt-4">
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={!!s.shuffleBlocks} onChange={(e) => updateSettings({ shuffleBlocks: e.target.checked })} />
            Случайный порядок блоков
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={s.allowReview !== false} onChange={(e) => updateSettings({ allowReview: e.target.checked })} />
            Разрешить просмотр результатов
          </label>
        </div>
        <div className="mt-4">
          <div className="flex items-center justify-between">
            <div className="font-medium">Таймер (сек) по попыткам</div>
            <Button variant="outline" onClick={() => updateSettings({ attemptTimeLimitsSeconds: [...(s.attemptTimeLimitsSeconds || []), null] })}>+ Добавить попытку</Button>
          </div>
          <div className="grid md:grid-cols-2 gap-3 mt-3">
            {(s.attemptTimeLimitsSeconds || []).map((v, idx) => (
              <div key={idx} className="flex items-center gap-2">
                <Badge>Попытка {idx + 1}</Badge>
                <Input type="number" min={1} placeholder="без таймера" value={v ?? ''} onChange={(e) => {
                  const next = [...(s.attemptTimeLimitsSeconds || [])];
                  next[idx] = e.target.value === '' ? null : Number(e.target.value || 0);
                  updateSettings({ attemptTimeLimitsSeconds: next });
                }} />
                <Button variant="outline" onClick={() => {
                  const next = [...(s.attemptTimeLimitsSeconds || [])];
                  next.splice(idx, 1);
                  updateSettings({ attemptTimeLimitsSeconds: next });
                }}>Удалить</Button>
              </div>
            ))}
          </div>
        </div>
      </Card>

      <Card>
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-xl font-semibold">Блоки задания</h2>
          <div className="flex flex-wrap gap-2">
            {BLOCK_TYPES.map(t => (
              <Button key={t.value} variant="outline" onClick={() => addBlock(t.value)}>{t.label}</Button>
            ))}
          </div>
        </div>

        <div className="space-y-4">
          {list.map((b, idx) => (
            <div key={`${b.id || 'new'}_${idx}`} className="rounded-xl border border-neutral-200 dark:border-neutral-800 p-4 bg-[rgb(var(--card))] space-y-4">
              <div className="flex items-center justify-between gap-3 flex-wrap">
                <div className="flex items-center gap-2 flex-wrap">
                  <Badge>Блок {idx + 1}</Badge>
                  <Select value={b.kind || 'info'} onChange={(e) => { const kind = e.target.value; updateBlock(idx, { kind, score: kind === 'info' ? 0 : (Number.isFinite(Number(b.score)) && Number(b.score) > 0 ? Number(b.score) : 1) }); }}>
                    {BLOCK_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
                  </Select>
                  <Field label="Баллы">
                    <Input type="number" min={0} value={b.score ?? 1} onChange={(e) => updateBlock(idx, { score: Number(e.target.value || 0) })} className="w-24" />
                  </Field>
                </div>
                <div className="flex items-center gap-2">
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={b.isRequired !== false} onChange={(e) => updateBlock(idx, { isRequired: e.target.checked })} />
                    Обязательный
                  </label>
                  <Button variant="outline" onClick={() => moveBlock(idx, -1)}>↑</Button>
                  <Button variant="outline" onClick={() => moveBlock(idx, 1)}>↓</Button>
                  <Button variant="outline" onClick={() => removeBlock(idx)}>Удалить</Button>
                </div>
              </div>

              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Короткий заголовок / вопрос">
                  <Input value={b.prompt || ''} onChange={(e) => updateBlock(idx, { prompt: e.target.value })} placeholder="Например: Найди корни уравнения" />
                </Field>
                <div className="text-sm text-neutral-500 flex items-end">
                  Для сложного условия используй rich-редактор ниже: формулы, картинки, файлы, шаги, подсказки.
                </div>
                <div className="md:col-span-2">
                  <Field label="Rich-условие блока">
                    <StatementEditor value={b.promptContentJson || ''} onChange={(v) => updateBlock(idx, { promptContentJson: v })} />
                  </Field>
                </div>
              </div>

              {(b.kind === 'single-choice' || b.kind === 'multi-choice') && renderChoiceEditor(b, idx, b.kind === 'multi-choice')}
              {b.kind === 'number' && renderTextEditor(b, idx, true)}
              {b.kind === 'expression' && renderTextEditor(b, idx, false)}
              {b.kind === 'set' && renderTextEditor(b, idx, false)}
              {b.kind === 'order' && renderOrderEditor(b, idx)}
              {b.kind === 'match' && renderMatchEditor(b, idx)}
              {b.kind === 'info' && (
                <div className="text-sm text-neutral-500">Инфо-блок не проверяется, но может содержать rich-контент, картинки, файлы, шаги решения, подсказки и формулы.</div>
              )}
            </div>
          ))}
          {list.length === 0 && <div className="text-sm text-neutral-500">Пока нет блоков. Добавь хотя бы один инфо-блок или ответный блок.</div>}
        </div>
      </Card>
    </div>
  );
}
