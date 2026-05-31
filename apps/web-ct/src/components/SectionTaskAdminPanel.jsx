import React, { useEffect, useMemo, useState } from 'react';
import {
  CheckCircle2,
  Copy,
  Download,
  Edit3,
  HelpCircle,
  Loader2,
  Plus,
  RefreshCcw,
  Save,
  Trash2,
  Upload,
  X,
} from 'lucide-react';
import {
  createQuizTask,
  deleteQuizTask,
  getAdminQuizTasks,
  updateQuizTask,
} from '../api/quiz';
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

function cleanJsonText(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  return text
    .replace(/^```(?:json)?/i, '')
    .replace(/```$/i, '')
    .trim();
}

function parseJsonValue(value, fallback = {}) {
  if (value === undefined || value === null || value === '') return fallback;
  if (typeof value === 'object') return value;
  try { return JSON.parse(String(value)); } catch { return fallback; }
}

function stringifyPretty(value) {
  return JSON.stringify(value, null, 2);
}

function pickText(item, names) {
  for (const name of names) {
    const value = item?.[name];
    if (value !== undefined && value !== null && String(value).trim()) return String(value).trim();
  }
  return '';
}

function explanationToText(value) {
  const data = parseJsonValue(value, value || {});
  if (typeof data === 'string') return data;
  if (data.text) return String(data.text);
  if (data.markdown) return String(data.markdown);
  if (Array.isArray(data.blocks)) {
    return data.blocks.map((block) => block?.text || block?.content || '').filter(Boolean).join('\n');
  }
  return '';
}

function answerToText(value) {
  const data = parseJsonValue(value, value || '');
  if (typeof data === 'string') return data;
  if (Array.isArray(data.selected)) return data.selected[0] || '';
  if (Array.isArray(data.values)) return data.values[0] || '';
  if (Array.isArray(data.answers)) return data.answers[0] || '';
  if (data.value) return String(data.value);
  if (data.text) return String(data.text);
  return '';
}

function tagsToText(tagsJson) {
  const tags = parseJsonValue(tagsJson, []);
  return Array.isArray(tags) ? tags.join(', ') : '';
}

function isBSection(sectionCode) {
  return String(sectionCode || '').trim().toUpperCase().startsWith('B');
}

function isTextAnswerType(type, sectionCode) {
  return isBSection(sectionCode) || type === 'text-answer' || type === 'text';
}

function normalizeTextForCompare(value) {
  return String(value || '')
    .trim()
    .toLowerCase()
    .replaceAll('ё', 'е')
    .replace(/[.,;:!?]+$/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function defaultTaskType(sectionCode) {
  return isBSection(sectionCode) ? 'text-answer' : 'single-choice';
}

function makeForm(sectionCode) {
  const type = defaultTaskType(sectionCode);
  return {
    id: '',
    slug: '',
    type,
    title: '',
    prompt: '',
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode,
    difficulty: '1',
    optionsText: type === 'text-answer' ? '' : '1\n2\n3\n4',
    correctAnswer: '',
    explanation: '',
    tagsText: `${sectionCode}, ЦТ`,
    sourceName: '',
    sourceYear: '',
    isPublished: true,
  };
}

function formFromDetails(details, sectionCode) {
  const task = details?.task || details || {};
  const data = parseJsonValue(details?.dataJson || task.dataJson, {});
  const correctAnswer = details?.correctAnswerJson || task.correctAnswerJson || task.correctAnswer;
  const explanation = details?.explanationJson || task.explanationJson || task.explanation;
  const options = Array.isArray(data.options) ? data.options : [];
  const type = isTextAnswerType(task.type, sectionCode) ? 'text-answer' : (task.type || 'single-choice');
  return {
    id: task.id || '',
    slug: task.slug || '',
    type,
    title: task.title || '',
    prompt: task.prompt || '',
    subjectCode: task.subjectCode || SUBJECT_CODE,
    examCode: task.examCode || EXAM_CODE,
    sectionCode: normalizeSectionCode(task.sectionCode || sectionCode) || sectionCode,
    difficulty: String(task.difficulty || 1),
    optionsText: type === 'text-answer' ? '' : (options.join('\n') || '1\n2\n3\n4'),
    correctAnswer: answerToText(correctAnswer),
    explanation: explanationToText(explanation),
    tagsText: tagsToText(task.tagsJson),
    sourceName: task.sourceName || '',
    sourceYear: task.sourceYear ? String(task.sourceYear) : '',
    isPublished: task.isPublished !== false,
  };
}

function payloadFromForm(form, sectionCode) {
  const type = isTextAnswerType(form.type, sectionCode) ? 'text-answer' : (form.type || 'single-choice');
  const isChoice = !isTextAnswerType(type, sectionCode);
  const options = splitValues(form.optionsText);
  const slug = form.slug.trim() || `${sectionCode.toLowerCase()}-${slugify(form.title)}`;
  return {
    slug,
    type,
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
  };
}

function validateForm(form, sectionCode) {
  const type = form.type || 'single-choice';
  const isChoice = !isTextAnswerType(type, sectionCode);
  const options = splitValues(form.optionsText);
  if (!sectionCode) return 'Выбери раздел с кодом A1, B5 и т.п.';
  if (!form.title.trim()) return 'Заполни название задания.';
  if (!form.prompt.trim()) return 'Заполни текст задания.';
  if (isChoice && options.length === 0) return 'Для задания с выбором ответа добавь варианты.';
  if (!form.correctAnswer.trim()) return 'Укажи правильный ответ.';
  if (!form.explanation.trim()) return 'Заполни объяснение: ученик должен видеть, почему ответ правильный или неправильный.';
  if (isChoice && !options.some((x) => normalizeTextForCompare(x) === normalizeTextForCompare(form.correctAnswer))) {
    return 'Правильный ответ должен совпадать с одним из вариантов.';
  }
  return '';
}

function parseTaskImport(value) {
  const cleaned = cleanJsonText(value);
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

function normalizeImportedTask(item, index, sectionCode) {
  const task = item?.task && typeof item.task === 'object' ? item.task : item;
  const data = parseJsonValue(item?.data ?? item?.dataJson ?? task?.data ?? task?.dataJson, {});
  const correct = item?.correctAnswer ?? item?.correctAnswerJson ?? task?.correctAnswer ?? task?.correctAnswerJson;
  const explanationRaw = item?.explanation ?? item?.explanationJson ?? task?.explanation ?? task?.explanationJson;
  const options = Array.isArray(data.options)
    ? data.options.map((option) => String(option).trim()).filter(Boolean)
    : Array.isArray(item?.options)
      ? item.options.map((option) => String(option).trim()).filter(Boolean)
      : splitValues(item?.optionsText || item?.variants || item?.answers);
  const type = isTextAnswerType(task?.type, sectionCode) || options.length === 0 ? 'text-answer' : 'single-choice';
  const title = pickText(task, ['title', 'name']) || `${sectionCode}. Задание ${index + 1}`;
  const prompt = pickText(task, ['prompt', 'question', 'text', 'body']);
  const correctAnswer = answerToText(correct) || pickText(item, ['correctAnswer', 'answer', 'correct', 'rightAnswer']);
  const explanation = explanationToText(explanationRaw) || pickText(item, ['explanation', 'why', 'comment', 'explanationText']);
  const tagsSource = task?.tagsJson ?? task?.tags ?? item?.tagsJson ?? item?.tags ?? `${sectionCode}, ЦТ`;
  const tags = Array.isArray(tagsSource) ? tagsSource.map((tag) => String(tag).trim()).filter(Boolean) : splitValues(tagsToText(tagsSource) || tagsSource);
  const difficulty = Number(task?.difficulty || item?.difficulty) || 1;

  if (!prompt) throw new Error(`Задание ${index + 1}: нет текста задания.`);
  if (!correctAnswer) throw new Error(`Задание ${index + 1}: нет правильного ответа.`);
  if (!explanation) throw new Error(`Задание ${index + 1}: нет объяснения.`);
  if (type !== 'text-answer' && !options.length) throw new Error(`Задание ${index + 1}: нет вариантов ответа.`);
  if (type !== 'text-answer' && !options.some((option) => normalizeTextForCompare(option) === normalizeTextForCompare(correctAnswer))) {
    throw new Error(`Задание ${index + 1}: правильный ответ должен совпадать с одним из вариантов.`);
  }

  return {
    slug: pickText(task, ['slug']) || `${sectionCode.toLowerCase()}-${slugify(title)}-${Date.now()}-${index + 1}`,
    type,
    title,
    prompt,
    subjectCode: task?.subjectCode || SUBJECT_CODE,
    examCode: task?.examCode || EXAM_CODE,
    sectionCode,
    difficulty,
    tags,
    sourceName: task?.sourceName || item?.sourceName || null,
    sourceYear: (task?.sourceYear || item?.sourceYear) ? Number(task?.sourceYear || item?.sourceYear) : null,
    data: type === 'text-answer' ? {} : { options },
    correctAnswer: type === 'text-answer' ? { value: correctAnswer } : { selected: [correctAnswer] },
    explanation: { text: explanation },
    isPublished: task?.isPublished !== false && item?.isPublished !== false,
  };
}

function exportItem(details) {
  const task = details.task || {};
  return {
    slug: task.slug,
    type: task.type,
    title: task.title,
    prompt: task.prompt,
    subjectCode: task.subjectCode,
    examCode: task.examCode,
    sectionCode: task.sectionCode,
    difficulty: task.difficulty,
    tags: parseJsonValue(task.tagsJson, []),
    sourceName: task.sourceName,
    sourceYear: task.sourceYear,
    data: parseJsonValue(details.dataJson, {}),
    correctAnswer: parseJsonValue(details.correctAnswerJson, {}),
    explanation: parseJsonValue(details.explanationJson, {}),
    isPublished: task.isPublished,
  };
}

function makeImportExample(sectionCode) {
  const normalizedSection = String(sectionCode || 'A1').toUpperCase();
  const textAnswer = isBSection(normalizedSection);

  return stringifyPretty({
    schemaVersion: 1,
    sectionCode: normalizedSection,
    tasks: [
      textAnswer
        ? {
          slug: `${normalizedSection.toLowerCase()}-task-1`,
          title: `${normalizedSection}. Задание 1`,
          prompt: 'Запишите ответ словом или буквами.',
          type: 'text-answer',
          difficulty: 1,
          tags: [normalizedSection, 'ЦТ'],
          data: {},
          correctAnswer: { value: 'пример' },
          explanation: { text: 'Краткое объяснение правильного ответа.' },
          isPublished: true,
        }
        : {
          slug: `${normalizedSection.toLowerCase()}-task-1`,
          title: `${normalizedSection}. Задание 1`,
          prompt: 'Текст задания. Варианты можно писать в prompt или отдельно в data.options.',
          type: 'single-choice',
          difficulty: 1,
          tags: [normalizedSection, 'ЦТ'],
          data: { options: ['1', '2', '3', '4'] },
          correctAnswer: { selected: ['1'] },
          explanation: { text: 'Короткое объяснение правильного ответа.' },
          isPublished: true,
        },
    ],
  });
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

function Alert({ type = 'info', children }) {
  const cls = type === 'error'
    ? 'border-red-200 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100'
    : type === 'success'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100'
      : 'border-sky-200 bg-sky-50 text-sky-800 dark:border-sky-900 dark:bg-sky-950/30 dark:text-sky-100';
  return <div className={`mb-4 rounded-3xl border p-4 text-sm ${cls}`}>{children}</div>;
}

export default function SectionTaskAdminPanel({ selectedCourse }) {
  const sectionCode = normalizeSectionCode(selectedCourse?.sectionCode || '');
  const [tasks, setTasks] = useState([]);
  const [form, setForm] = useState(makeForm(sectionCode));
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [jsonText, setJsonText] = useState(() => makeImportExample(sectionCode));
  const [replaceOnImport, setReplaceOnImport] = useState(false);

  const canUse = Boolean(sectionCode);
  const isEdit = Boolean(form.id);
  const isChoice = !isTextAnswerType(form.type, sectionCode);
  const options = useMemo(() => splitValues(form.optionsText), [form.optionsText]);

  function setField(name, value) {
    setForm((prev) => ({ ...prev, [name]: value }));
  }

  function resetForm() {
    setForm(makeForm(sectionCode));
    setError('');
    setSuccess('');
  }

  async function loadTasks() {
    if (!sectionCode) return;
    setBusy('load');
    setError('');
    try {
      const data = await getAdminQuizTasks({
        subjectCode: selectedCourse?.subjectCode || SUBJECT_CODE,
        examCode: selectedCourse?.examCode || EXAM_CODE,
        sectionCode,
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
        const data = await getAdminQuizTasks({
          subjectCode: selectedCourse?.subjectCode || SUBJECT_CODE,
          examCode: selectedCourse?.examCode || EXAM_CODE,
          sectionCode,
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
    setJsonText(makeImportExample(sectionCode));
    refresh();
    return () => { cancelled = true; };
  }, [sectionCode, selectedCourse?.subjectCode, selectedCourse?.examCode]);

  async function saveTask() {
    const validation = validateForm(form, sectionCode);
    if (validation) { setError(validation); return; }
    const payload = payloadFromForm(form, sectionCode);
    setBusy('save');
    setError('');
    setSuccess('');
    try {
      if (form.id) {
        await updateQuizTask(form.id, payload);
        setSuccess('Задание обновлено. Изменения доступны в этом же разделе.');
      } else {
        await createQuizTask(payload);
        setSuccess('Задание создано. Оно появится у ученика в случайной выдаче этого номера.');
      }
      setForm(makeForm(sectionCode));
      await loadTasks();
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось сохранить задание.');
    } finally {
      setBusy('');
    }
  }

  async function removeTask(details) {
    const task = details?.task;
    if (!task?.id) return;
    const ok = window.confirm(`Удалить задание "${task.title}"? Это удалит само задание и связанные попытки/прогресс по нему.`);
    if (!ok) return;
    setBusy(`delete:${task.id}`);
    setError('');
    setSuccess('');
    try {
      await deleteQuizTask(task.id);
      if (form.id === task.id) setForm(makeForm(sectionCode));
      setSuccess('Задание удалено.');
      await loadTasks();
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось удалить задание.');
    } finally {
      setBusy('');
    }
  }

  function editTask(details) {
    setForm(formFromDetails(details, sectionCode));
    setError('');
    setSuccess('');
    setTimeout(() => document.getElementById('task-editor-form')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 0);
  }

  function exportTasks() {
    const exported = { schemaVersion: 1, sectionCode, tasks: tasks.map(exportItem) };
    setJsonText(stringifyPretty(exported));
    setSuccess(`Экспортировано в JSON: ${tasks.length}. Можно скопировать поле ниже.`);
  }

  async function copyJson() {
    try {
      await navigator.clipboard.writeText(jsonText);
      setSuccess('JSON скопирован в буфер обмена.');
    } catch {
      setError('Не удалось скопировать автоматически. Выдели JSON вручную и скопируй.');
    }
  }

  async function importTasks() {
    if (!sectionCode) return;
    setBusy('import');
    setError('');
    setSuccess('');
    try {
      const parsed = parseTaskImport(jsonText);
      const payloads = parsed.map((item, index) => normalizeImportedTask(item, index, sectionCode));
      if (replaceOnImport) {
        const ok = window.confirm(`Заменить все задания раздела ${sectionCode}? Сейчас будет удалено: ${tasks.length}. Потом будет импортировано: ${payloads.length}.`);
        if (!ok) return;
        for (const details of tasks) {
          if (details?.task?.id) await deleteQuizTask(details.task.id);
        }
      }
      for (const payload of payloads) {
        await createQuizTask(payload);
      }
      setSuccess(`${replaceOnImport ? 'Заменено' : 'Импортировано'} заданий: ${payloads.length}.`);
      setForm(makeForm(sectionCode));
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
        Чтобы создавать задания, открой конкретный номер части A или B: A1, A31, B5, B11 и т.п.
      </section>
    );
  }

  return (
    <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <div className="flex items-center gap-2"><Plus className="text-brand-600" /><h2 className="text-2xl font-bold tracking-tight">Задания для {sectionCode}</h2></div>
          <p className="mt-1 text-sm leading-6 text-neutral-500 dark:text-neutral-400">
            Тут редактируются задания выбранного номера. Количество заданий не ограничено: можно создавать сколько угодно карточек для A или B. Обычные пользователи эту панель и черновики не получают.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={exportTasks} disabled={busy !== '' || tasks.length === 0} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
            <Download size={16} /> Экспорт JSON
          </button>
          <button type="button" onClick={loadTasks} disabled={busy === 'load'} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
            {busy === 'load' ? <Loader2 size={16} className="animate-spin" /> : <RefreshCcw size={16} />}
            Обновить
          </button>
        </div>
      </div>

      {error ? <Alert type="error">{error}</Alert> : null}
      {success ? <Alert type="success">{success}</Alert> : null}

      <div className="grid gap-5 xl:grid-cols-[minmax(280px,0.8fr)_minmax(0,1.2fr)]">
        <div className="rounded-[1.5rem] border border-neutral-200 bg-neutral-50 p-3 dark:border-neutral-800 dark:bg-neutral-950">
          <div className="mb-3 flex items-center justify-between gap-2">
            <div className="text-sm font-black uppercase tracking-wide text-neutral-500 dark:text-neutral-400">Список</div>
            <div className="rounded-full bg-white px-3 py-1 text-xs font-bold dark:bg-neutral-900">{tasks.length}</div>
          </div>
          <div className="max-h-[620px] space-y-2 overflow-auto pr-1">
            {tasks.map((details) => {
              const task = details.task;
              const isCurrent = form.id === task.id;
              return (
                <article key={task.id} className={`rounded-2xl border p-3 transition ${isCurrent ? 'border-brand-400 bg-white shadow-sm dark:border-brand-700 dark:bg-neutral-900' : 'border-neutral-200 bg-white dark:border-neutral-800 dark:bg-neutral-900'}`}>
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <div className="line-clamp-2 font-bold leading-5">{task.title}</div>
                      <div className="mt-1 truncate text-xs text-neutral-500 dark:text-neutral-400">/{task.slug}</div>
                    </div>
                    {task.isPublished ? <CheckCircle2 size={16} className="shrink-0 text-emerald-600" /> : <span className="shrink-0 rounded-xl bg-amber-100 px-2 py-1 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-100">черновик</span>}
                  </div>
                  <div className="mt-2 line-clamp-2 text-sm text-neutral-600 dark:text-neutral-300">{task.prompt}</div>
                  <div className="mt-3 flex flex-wrap gap-2">
                    <button type="button" onClick={() => editTask(details)} className="inline-flex items-center gap-1 rounded-xl border border-neutral-200 bg-neutral-50 px-3 py-1.5 text-xs font-bold hover:border-brand-300 dark:border-neutral-800 dark:bg-neutral-950">
                      <Edit3 size={13} /> Изменить
                    </button>
                    <button type="button" onClick={() => removeTask(details)} disabled={busy === `delete:${task.id}`} className="inline-flex items-center gap-1 rounded-xl border border-red-200 bg-red-50 px-3 py-1.5 text-xs font-bold text-red-700 hover:border-red-300 disabled:opacity-60 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">
                      {busy === `delete:${task.id}` ? <Loader2 size={13} className="animate-spin" /> : <Trash2 size={13} />} Удалить
                    </button>
                  </div>
                </article>
              );
            })}
            {!tasks.length ? <div className="rounded-2xl border border-dashed border-neutral-200 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">Для {sectionCode} пока нет заданий.</div> : null}
          </div>
        </div>

        <div id="task-editor-form" className="rounded-[1.5rem] border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
          <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-sm font-black uppercase tracking-wide text-brand-700 dark:text-brand-300">{isEdit ? 'Редактирование' : 'Новое задание'}</div>
              <h3 className="text-xl font-black">{isEdit ? form.title || 'Задание' : `Создать задание для ${sectionCode}`}</h3>
            </div>
            {isEdit ? <button type="button" onClick={resetForm} className="btn-outline inline-flex items-center gap-2"><X size={16} /> Отменить</button> : null}
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Название" required hint="Короткое понятное название карточки."><Input value={form.title} onChange={(e) => setField('title', e.target.value)} /></Field>
            <Field label="Тип задания" hint={isBSection(sectionCode) ? 'Для B-части всегда используется текстовое поле: ответ словом или буквами.' : ''}>
              {isBSection(sectionCode) ? (
                <div className="w-full rounded-2xl border border-neutral-200 bg-white px-3 py-2.5 font-semibold text-neutral-700 dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-200">
                  Краткий текстовый ответ
                </div>
              ) : (
                <select
                  value={form.type}
                  onChange={(e) => setForm((prev) => ({
                    ...prev,
                    type: e.target.value,
                    optionsText: e.target.value === 'text-answer' ? '' : (prev.optionsText || '1\n2\n3\n4'),
                  }))}
                  className="w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950"
                >
                  <option value="single-choice">Выбор ответа</option>
                  <option value="text-answer">Краткий ответ</option>
                </select>
              )}
            </Field>
            <div className="md:col-span-2"><Field label="Текст задания" required><Textarea rows={5} value={form.prompt} onChange={(e) => setField('prompt', e.target.value)} /></Field></div>
            {isChoice ? (
              <div className="md:col-span-2"><Field label="Варианты ответа" required hint={`Каждый вариант с новой строки или через запятую. Сейчас вариантов: ${options.length}.`}><Textarea rows={5} value={form.optionsText} onChange={(e) => setField('optionsText', e.target.value)} /></Field></div>
            ) : null}
            <Field label="Правильный ответ" required hint={isChoice ? 'Должен совпадать с одним из вариантов.' : 'Для B-части и кратких ответов вводится обычный текст. При проверке не важен регистр, лишние пробелы по краям и ё/е.'}><Input value={form.correctAnswer} onChange={(e) => setField('correctAnswer', e.target.value)} /></Field>
            <Field label="Сложность"><Input type="number" min="1" max="5" value={form.difficulty} onChange={(e) => setField('difficulty', e.target.value)} /></Field>
            <div className="md:col-span-2"><Field label="Объяснение после проверки" required hint="Показывается ученику только после проверки ответа."><Textarea rows={3} value={form.explanation} onChange={(e) => setField('explanation', e.target.value)} /></Field></div>
            <Field label="Теги" hint="Через запятую: A1, орфография, ЦТ."><Input value={form.tagsText} onChange={(e) => setField('tagsText', e.target.value)} /></Field>
            <Field label="Slug" hint="Можно оставить пустым — сгенерируется автоматически."><Input value={form.slug} onChange={(e) => setField('slug', e.target.value)} /></Field>
            <Field label="Источник"><Input value={form.sourceName} onChange={(e) => setField('sourceName', e.target.value)} placeholder="например: авторское / ЦТ 2023" /></Field>
            <Field label="Год источника"><Input type="number" value={form.sourceYear} onChange={(e) => setField('sourceYear', e.target.value)} /></Field>
            <label className="flex items-center gap-2 pt-2 md:col-span-2"><input type="checkbox" checked={form.isPublished} onChange={(e) => setField('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликовано</span><span className="text-xs text-neutral-500">если выключить, обычный пользователь это задание не увидит</span></label>
          </div>

          <button type="button" onClick={saveTask} disabled={busy === 'save'} className="btn-primary mt-5 inline-flex items-center gap-2 disabled:opacity-60">
            {busy === 'save' ? <Loader2 size={18} className="animate-spin" /> : isEdit ? <Save size={18} /> : <Plus size={18} />}
            {isEdit ? 'Сохранить изменения' : `Создать задание для ${sectionCode}`}
          </button>
        </div>
      </div>

      <details className="mt-6 rounded-[1.5rem] border border-dashed border-brand-200 bg-brand-50/50 p-4 dark:border-brand-900 dark:bg-brand-950/20">
        <summary className="cursor-pointer list-none">
          <span className="inline-flex items-center gap-2 text-base font-black"><Upload size={18} /> JSON: экспорт / импорт всех заданий раздела</span>
          <span className="ml-2 text-sm text-neutral-500 dark:text-neutral-400">доступно только через admin API</span>
        </summary>
        <div className="mt-4 grid gap-4 lg:grid-cols-[0.8fr_1.2fr]">
          <div className="rounded-3xl bg-white p-4 text-sm leading-6 shadow-sm dark:bg-neutral-950">
            <div className="font-bold">Как пользоваться</div>
            <p className="mt-2 text-neutral-600 dark:text-neutral-300">Экспорт берёт все задания текущего номера вместе с вариантами, правильными ответами и объяснениями. Импорт всегда привязывает задания к текущему номеру {sectionCode}. Для B-части варианты не нужны: ответ хранится как текст в correctAnswer.value.</p>
            <div className="mt-4 flex flex-wrap gap-2">
              <button type="button" onClick={exportTasks} disabled={tasks.length === 0 || busy !== ''} className="btn-outline inline-flex items-center gap-2 bg-white disabled:opacity-60 dark:bg-neutral-950"><Download size={16} /> Экспортировать</button>
              <button type="button" onClick={copyJson} className="btn-outline inline-flex items-center gap-2 bg-white dark:bg-neutral-950"><Copy size={16} /> Скопировать</button>
            </div>
            <label className="mt-4 flex items-start gap-2 rounded-2xl border border-red-200 bg-red-50 p-3 text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">
              <input type="checkbox" checked={replaceOnImport} onChange={(e) => setReplaceOnImport(e.target.checked)} className="mt-1" />
              <span><b>Заменить все задания раздела при импорте.</b><br />Без галочки импорт добавляет новые задания. С галочкой старые задания и их попытки будут удалены после подтверждения.</span>
            </label>
          </div>
          <div>
            <Field label="JSON заданий" hint="Поддерживается массив или объект { tasks: [...] }. Для безопасности импорт идёт только через защищённый admin endpoint.">
              <Textarea rows={18} value={jsonText} onChange={(e) => setJsonText(e.target.value)} spellCheck={false} className="font-mono text-xs" />
            </Field>
            <button type="button" onClick={importTasks} disabled={busy === 'import'} className="btn-primary mt-3 inline-flex items-center gap-2 disabled:opacity-60">
              {busy === 'import' ? <Loader2 size={18} className="animate-spin" /> : <Upload size={18} />}
              Импортировать JSON для {sectionCode}
            </button>
          </div>
        </div>
      </details>
    </section>
  );
}
