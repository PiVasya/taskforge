import React, { useEffect, useMemo, useState } from 'react';
import { CheckCircle2, HelpCircle, Loader2, Plus, RefreshCcw, Sparkles, Upload } from 'lucide-react';
import { createQuizTask, getQuizTasks } from '../api/quiz';
import { EXAM_CODE, SUBJECT_CODE, normalizeSectionCode } from '../data/ctSections';

function slugify(text) {
  const map = { а:'a',б:'b',в:'v',г:'g',д:'d',е:'e',ё:'e',ж:'zh',з:'z',и:'i',й:'y',к:'k',л:'l',м:'m',н:'n',о:'o',п:'p',р:'r',с:'s',т:'t',у:'u',ф:'f',х:'h',ц:'c',ч:'ch',ш:'sh',щ:'sch',ы:'y',э:'e',ю:'yu',я:'ya',ь:'',ъ:'' };
  return String(text || '')
    .trim()
    .toLowerCase()
    .split('')
    .map((c) => map[c] ?? c)
    .join('')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '') || `task-${Date.now()}`;
}

function splitValues(value) {
  return String(value || '')
    .split(/\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function cleanAiJson(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  return text
    .replace(/^```(?:json)?/i, '')
    .replace(/```$/i, '')
    .trim();
}

function parseAiTasks(value) {
  const cleaned = cleanAiJson(value);
  if (!cleaned) throw new Error('Вставь JSON с заданиями.');
  let parsed;
  try {
    parsed = JSON.parse(cleaned);
  } catch {
    const firstArray = cleaned.indexOf('[');
    const lastArray = cleaned.lastIndexOf(']');
    const firstObject = cleaned.indexOf('{');
    const lastObject = cleaned.lastIndexOf('}');
    const arrayCandidate = firstArray >= 0 && lastArray > firstArray ? cleaned.slice(firstArray, lastArray + 1) : '';
    const objectCandidate = firstObject >= 0 && lastObject > firstObject ? cleaned.slice(firstObject, lastObject + 1) : '';
    parsed = JSON.parse(arrayCandidate || objectCandidate);
  }
  const items = Array.isArray(parsed) ? parsed : parsed.tasks;
  if (!Array.isArray(items) || !items.length) throw new Error('JSON должен быть массивом заданий или объектом { "tasks": [...] }.');
  return items;
}

function pickText(item, names) {
  for (const name of names) {
    const value = item?.[name];
    if (value !== undefined && value !== null && String(value).trim()) return String(value).trim();
  }
  return '';
}

function normalizeAiTask(item, index, sectionCode) {
  const options = Array.isArray(item?.options)
    ? item.options.map((option) => String(option).trim()).filter(Boolean)
    : splitValues(item?.optionsText || item?.variants || item?.answers);
  const type = item?.type === 'text-answer' || item?.type === 'text' || options.length === 0 ? 'text-answer' : 'single-choice';
  const title = pickText(item, ['title', 'name']) || `${sectionCode}. Задание ${index + 1}`;
  const prompt = pickText(item, ['prompt', 'question', 'text', 'body']);
  const correctAnswer = pickText(item, ['correctAnswer', 'answer', 'correct', 'rightAnswer']);
  const explanation = pickText(item, ['explanation', 'why', 'comment', 'explanationText']);
  const tags = Array.isArray(item?.tags) ? item.tags.map((tag) => String(tag).trim()).filter(Boolean) : splitValues(item?.tags || `${sectionCode}, ЦТ`);
  const difficulty = Number(item?.difficulty) || 1;
  if (!prompt) throw new Error(`Задание ${index + 1}: нет текста задания.`);
  if (!correctAnswer) throw new Error(`Задание ${index + 1}: нет правильного ответа.`);
  if (!explanation) throw new Error(`Задание ${index + 1}: нет объяснения.`);
  if (type !== 'text-answer' && !options.length) throw new Error(`Задание ${index + 1}: нет вариантов ответа.`);
  if (type !== 'text-answer' && !options.some((option) => option.toLowerCase() === correctAnswer.toLowerCase())) {
    throw new Error(`Задание ${index + 1}: правильный ответ должен совпадать с одним из вариантов.`);
  }
  return {
    slug: pickText(item, ['slug']) || `${sectionCode.toLowerCase()}-${slugify(title)}-${Date.now()}-${index + 1}`,
    type,
    title,
    prompt,
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode,
    difficulty,
    tags,
    sourceName: pickText(item, ['sourceName', 'source']) || null,
    sourceYear: item?.sourceYear ? Number(item.sourceYear) : null,
    data: type === 'text-answer' ? {} : { options },
    correctAnswer: type === 'text-answer' ? { value: correctAnswer } : { selected: [correctAnswer] },
    explanation: { text: explanation },
    isPublished: item?.isPublished !== false,
  };
}

function makeAiImportExample(sectionCode) {
  return JSON.stringify([
    {
      title: `${sectionCode || 'A1'}. Безударная гласная в корне`,
      prompt: 'Укажите слово, в котором пропущена проверяемая безударная гласная корня.\n1) р..сток\n2) прик..саться\n3) пол..гать\n4) л..сной',
      type: 'single-choice',
      options: ['1', '2', '3', '4'],
      correctAnswer: '4',
      explanation: 'В слове «лесной» гласная проверяется словом «лес». Остальные слова относятся к корням с чередованием.',
      difficulty: 1,
      tags: [sectionCode || 'A1', 'орфография']
    }
  ], null, 2);
}

function Field({ label, hint, children, required = false }) {
  return (
    <label className="block">
      <span className="text-sm font-semibold">
        {label}{required ? <span className="text-red-500"> *</span> : null}
      </span>
      <div className="mt-1">{children}</div>
      {hint ? <div className="mt-1 flex gap-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400"><HelpCircle size={13} className="mt-0.5 shrink-0" />{hint}</div> : null}
    </label>
  );
}

function Input(props) {
  return <input {...props} className={`w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950 ${props.className || ''}`} />;
}

function Textarea(props) {
  return <textarea {...props} className={`w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950 ${props.className || ''}`} />;
}

function makeForm(sectionCode) {
  return {
    slug: '',
    type: 'single-choice',
    title: '',
    prompt: '',
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode,
    difficulty: '1',
    optionsText: '1\n2\n3\n4\n5',
    correctAnswer: '',
    explanation: '',
    tagsText: `${sectionCode}, ЦТ`,
    sourceName: '',
    sourceYear: '',
    isPublished: true,
  };
}

export default function SectionTaskAdminPanel({ selectedCourse }) {
  const sectionCode = normalizeSectionCode(selectedCourse?.sectionCode || '');
  const [tasks, setTasks] = useState([]);
  const [form, setForm] = useState(makeForm(sectionCode));
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [bulkText, setBulkText] = useState(() => makeAiImportExample(sectionCode));

  const canUse = Boolean(sectionCode);
  const options = useMemo(() => splitValues(form.optionsText), [form.optionsText]);
  const isChoice = form.type !== 'text-answer';
  const importExample = useMemo(() => makeAiImportExample(sectionCode), [sectionCode]);

  function setField(name, value) {
    setForm((prev) => ({ ...prev, [name]: value }));
  }

  async function loadTasks() {
    if (!sectionCode) return;
    setBusy('load');
    setError('');
    try {
      const data = await getQuizTasks({
        subjectCode: selectedCourse?.subjectCode || SUBJECT_CODE,
        examCode: selectedCourse?.examCode || EXAM_CODE,
        sectionCode,
        includeDraft: true,
      });
      setTasks(data || []);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось загрузить задания раздела.');
    } finally {
      setBusy('');
    }
  }

  useEffect(() => {
    let cancelled = false;
    async function refresh() {
      if (!sectionCode) return;
      setBusy('load');
      setError('');
      try {
        const data = await getQuizTasks({
          subjectCode: selectedCourse?.subjectCode || SUBJECT_CODE,
          examCode: selectedCourse?.examCode || EXAM_CODE,
          sectionCode,
          includeDraft: true,
        });
        if (!cancelled) setTasks(data || []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить задания раздела.');
      } finally {
        if (!cancelled) setBusy('');
      }
    }
    setForm(makeForm(sectionCode));
    setTasks([]);
    setError('');
    setSuccess('');
    setBulkText(makeAiImportExample(sectionCode));
    refresh();
    return () => { cancelled = true; };
  }, [sectionCode, selectedCourse?.subjectCode, selectedCourse?.examCode]);

  function validate() {
    if (!sectionCode) return 'Выбери раздел с кодом A1, B5 и т.п.';
    if (!form.title.trim()) return 'Заполни название задания.';
    if (!form.prompt.trim()) return 'Заполни текст задания.';
    if (isChoice && options.length === 0) return 'Для задания с выбором ответа добавь варианты.';
    if (!form.correctAnswer.trim()) return 'Укажи правильный ответ.';
    if (!form.explanation.trim()) return 'Заполни объяснение: ученик должен видеть, почему ответ правильный или неправильный.';
    if (isChoice && !options.some((x) => x.toLowerCase() === form.correctAnswer.trim().toLowerCase())) {
      return 'Правильный ответ должен совпадать с одним из вариантов.';
    }
    return '';
  }

  async function saveTask() {
    const validation = validate();
    if (validation) { setError(validation); return; }
    const slug = form.slug.trim() || `${sectionCode.toLowerCase()}-${slugify(form.title)}`;
    setBusy('save');
    setError('');
    setSuccess('');
    try {
      await createQuizTask({
        slug,
        type: form.type,
        title: form.title.trim(),
        prompt: form.prompt.trim(),
        subjectCode: form.subjectCode.trim() || SUBJECT_CODE,
        examCode: form.examCode.trim() || EXAM_CODE,
        sectionCode,
        difficulty: Number(form.difficulty) || 1,
        tags: splitValues(form.tagsText),
        sourceName: form.sourceName.trim() || null,
        sourceYear: form.sourceYear ? Number(form.sourceYear) : null,
        data: isChoice ? { options } : {},
        correctAnswer: isChoice ? { selected: [form.correctAnswer.trim()] } : { value: form.correctAnswer.trim() },
        explanation: { text: form.explanation.trim() },
        isPublished: form.isPublished,
      });
      setSuccess('Задание создано. Оно появится у ученика в случайной выдаче этого номера.');
      setForm(makeForm(sectionCode));
      await loadTasks();
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось создать задание.');
    } finally {
      setBusy('');
    }
  }

  async function importAiTasks() {
    if (!sectionCode) return;
    setBusy('bulk');
    setError('');
    setSuccess('');
    try {
      const parsed = parseAiTasks(bulkText);
      const payloads = parsed.map((item, index) => normalizeAiTask(item, index, sectionCode));
      for (const payload of payloads) {
        await createQuizTask(payload);
      }
      setSuccess(`Импортировано заданий: ${payloads.length}. Они привязаны к ${sectionCode}.`);
      await loadTasks();
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось импортировать задания.');
    } finally {
      setBusy('');
    }
  }

  if (!canUse) {
    return (
      <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 text-sm text-neutral-500 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-400">
        Чтобы создавать задания, выбери слева конкретный номер с sectionCode: A1, A2, B5 и т.п.
      </section>
    );
  }

  return (
    <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <div className="flex items-center gap-2"><Plus className="text-brand-600" /><h2 className="text-2xl font-bold tracking-tight">Задания для {sectionCode}</h2></div>
          <p className="mt-1 text-sm leading-6 text-neutral-500 dark:text-neutral-400">
            Здесь создаются задания именно для выбранного номера. Ученик увидит случайные опубликованные задания под HTML-конспектом.
          </p>
        </div>
        <button type="button" onClick={loadTasks} disabled={busy === 'load'} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
          {busy === 'load' ? <Loader2 size={16} className="animate-spin" /> : <RefreshCcw size={16} />}
          Обновить
        </button>
      </div>

      {error ? <div className="mb-4 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">{error}</div> : null}
      {success ? <div className="mb-4 rounded-3xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">{success}</div> : null}

      <div className="mb-5 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {tasks.map((task) => (
          <div key={task.id} className="rounded-3xl border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
            <div className="flex items-start justify-between gap-2">
              <div className="font-semibold">{task.title}</div>
              {task.isPublished ? <CheckCircle2 size={16} className="shrink-0 text-emerald-600" /> : <span className="shrink-0 rounded-xl bg-amber-100 px-2 py-1 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-100">черновик</span>}
            </div>
            <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">/{task.slug}</div>
            <div className="mt-2 line-clamp-2 text-sm text-neutral-600 dark:text-neutral-300">{task.prompt}</div>
          </div>
        ))}
        {!tasks.length ? <div className="rounded-3xl border border-dashed border-neutral-200 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">Для {sectionCode} пока нет заданий.</div> : null}
      </div>

      <div className="grid gap-4 md:grid-cols-2">
        <Field label="Название" required hint="Например: A1. Проверка безударной гласной"><Input value={form.title} onChange={(e) => setField('title', e.target.value)} /></Field>
        <Field label="Slug" hint="Можно оставить пустым — сгенерируется автоматически."><Input value={form.slug} onChange={(e) => setField('slug', e.target.value)} /></Field>
        <Field label="Тип задания"><select value={form.type} onChange={(e) => setField('type', e.target.value)} className="w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950"><option value="single-choice">Выбор ответа</option><option value="text-answer">Краткий ответ</option></select></Field>
        <Field label="Сложность"><Input type="number" min="1" max="5" value={form.difficulty} onChange={(e) => setField('difficulty', e.target.value)} /></Field>
        <div className="md:col-span-2"><Field label="Текст задания" required><Textarea rows={4} value={form.prompt} onChange={(e) => setField('prompt', e.target.value)} /></Field></div>
        {isChoice ? (
          <div className="md:col-span-2"><Field label="Варианты ответа" required hint="Каждый вариант с новой строки или через запятую."><Textarea rows={5} value={form.optionsText} onChange={(e) => setField('optionsText', e.target.value)} /></Field></div>
        ) : null}
        <Field label="Правильный ответ" required hint={isChoice ? 'Должен совпадать с одним из вариантов.' : 'Для краткого ответа регистр не важен, ё/е нормализуется на бэке.'}><Input value={form.correctAnswer} onChange={(e) => setField('correctAnswer', e.target.value)} /></Field>
        <Field label="Теги" hint="Через запятую: A1, орфография, ЦТ."><Input value={form.tagsText} onChange={(e) => setField('tagsText', e.target.value)} /></Field>
        <div className="md:col-span-2"><Field label="Объяснение после проверки" required hint="Показывается в решениях и после неправильного ответа."><Textarea rows={3} value={form.explanation} onChange={(e) => setField('explanation', e.target.value)} /></Field></div>
        <Field label="Источник"><Input value={form.sourceName} onChange={(e) => setField('sourceName', e.target.value)} placeholder="например: авторское / ЦТ 2023" /></Field>
        <Field label="Год источника"><Input type="number" value={form.sourceYear} onChange={(e) => setField('sourceYear', e.target.value)} /></Field>
        <label className="flex items-center gap-2 pt-2 md:col-span-2"><input type="checkbox" checked={form.isPublished} onChange={(e) => setField('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликовано</span><span className="text-xs text-neutral-500">видно ученику</span></label>
      </div>

      <button type="button" onClick={saveTask} disabled={busy === 'save'} className="btn-primary mt-5 inline-flex items-center gap-2 disabled:opacity-60">
        {busy === 'save' ? <Loader2 size={18} className="animate-spin" /> : <Plus size={18} />}
        Создать задание для {sectionCode}
      </button>

      <details className="mt-6 rounded-[1.5rem] border border-dashed border-brand-200 bg-brand-50/50 p-4 dark:border-brand-900 dark:bg-brand-950/20">
        <summary className="cursor-pointer list-none">
          <span className="inline-flex items-center gap-2 text-base font-black"><Sparkles size={18} /> Импорт из нейронки</span>
          <span className="ml-2 text-sm text-neutral-500 dark:text-neutral-400">скрытый массовый ввод JSON</span>
        </summary>
        <div className="mt-4 grid gap-4 lg:grid-cols-[1fr_1.2fr]">
          <div className="rounded-3xl bg-white p-4 text-sm leading-6 shadow-sm dark:bg-neutral-950">
            <div className="font-bold">Формат</div>
            <p className="mt-2 text-neutral-600 dark:text-neutral-300">Вставь массив JSON. Можно передать массив сразу или объект с полем tasks. Все задания будут записаны в текущий номер {sectionCode}.</p>
            <pre className="mt-3 max-h-80 overflow-auto rounded-2xl bg-neutral-950 p-3 text-xs text-neutral-50">{importExample}</pre>
          </div>
          <div>
            <Field label="JSON заданий" hint="Удобно попросить нейронку вернуть только JSON без Markdown. Объяснение обязательно для каждого задания.">
              <Textarea rows={15} value={bulkText} onChange={(e) => setBulkText(e.target.value)} spellCheck={false} className="font-mono text-xs" />
            </Field>
            <button type="button" onClick={importAiTasks} disabled={busy === 'bulk'} className="btn-primary mt-3 inline-flex items-center gap-2 disabled:opacity-60">
              {busy === 'bulk' ? <Loader2 size={18} className="animate-spin" /> : <Upload size={18} />}
              Импортировать задания для {sectionCode}
            </button>
          </div>
        </div>
      </details>
    </section>
  );
}
