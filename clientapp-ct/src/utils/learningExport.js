import { getLearningConspect, getLearningConspects } from '../api/learning';
import { getQuizTask, getQuizTasks } from '../api/quiz';
import { EXAM_CODE, SUBJECT_CODE, normalizeSectionCode, parseSectionCode } from '../data/ctSections';

function safeJson(value, fallback) {
  if (!value) return fallback;
  if (typeof value === 'object') return value;
  try {
    return JSON.parse(value);
  } catch {
    return fallback;
  }
}

function escapeHtml(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

function stripUnsafeHtml(html) {
  const raw = String(html || '');
  return raw
    .replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, '')
    .replace(/<iframe\b[^<]*(?:(?!<\/iframe>)<[^<]*)*<\/iframe>/gi, '')
    .replace(/\son\w+=("[^"]*"|'[^']*'|[^\s>]+)/gi, '')
    .replace(/javascript:/gi, '');
}

function textFromUnknown(value) {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (Array.isArray(value)) return value.map(textFromUnknown).filter(Boolean).join(', ');
  if (typeof value === 'object') {
    return value.text || value.value || value.answer || value.label || value.title || JSON.stringify(value);
  }
  return String(value);
}

function explanationText(raw) {
  const data = safeJson(raw, raw || '');
  if (!data) return '';
  if (typeof data === 'string') return data;
  if (data.text) return data.text;
  if (data.markdown) return data.markdown;
  if (data.html) return data.html.replace(/<[^>]+>/g, ' ');
  if (Array.isArray(data.blocks)) {
    return data.blocks.map((block) => block?.text || block?.content || block?.title || '').filter(Boolean).join('\n');
  }
  return '';
}

function optionsFromData(dataJson) {
  const data = safeJson(dataJson, {});
  const options = Array.isArray(data.options) ? data.options : [];
  return options.map(textFromUnknown).filter(Boolean);
}

function extraTaskText(dataJson) {
  const data = safeJson(dataJson, {});
  return data.answerText || data.hint || data.description || data.context || '';
}

function renderTextBlock(text) {
  return String(text || '')
    .split('\n')
    .map((line) => `<p>${escapeHtml(line)}</p>`)
    .join('');
}

function renderBlock(block) {
  if (!block || typeof block !== 'object') return '';
  const title = block.title || block.text || '';

  switch (block.type) {
    case 'heading':
      return `<h2>${escapeHtml(block.text || block.title || '')}</h2>`;
    case 'text':
    case 'warning':
    case 'practice-intro':
      return `<section class="block"><h3>${escapeHtml(block.title || '')}</h3>${renderTextBlock(block.text || '')}</section>`;
    case 'rule-card':
      return `<section class="block"><h3>${escapeHtml(title)}</h3><ul>${(block.items || []).map((item) => `<li>${escapeHtml(textFromUnknown(item))}</li>`).join('')}</ul></section>`;
    case 'examples':
      return `<section class="block"><h3>${escapeHtml(title || 'Примеры')}</h3>${(block.items || []).map((item) => `<div class="example"><b>${escapeHtml(textFromUnknown(item?.source || item))}</b>${item?.answer ? `<div>${escapeHtml(item.answer)}</div>` : ''}</div>`).join('')}</section>`;
    case 'steps':
    case 'checklist':
      return `<section class="block"><h3>${escapeHtml(title)}</h3><ol>${(block.items || []).map((item) => `<li>${escapeHtml(textFromUnknown(item))}</li>`).join('')}</ol></section>`;
    case 'table':
      return `<section class="block"><h3>${escapeHtml(title)}</h3><table><thead><tr>${(block.columns || []).map((col) => `<th>${escapeHtml(col)}</th>`).join('')}</tr></thead><tbody>${(block.rows || []).map((row) => `<tr>${(row || []).map((cell) => `<td>${escapeHtml(textFromUnknown(cell))}</td>`).join('')}</tr>`).join('')}</tbody></table></section>`;
    case 'dictionary':
      return `<section class="block"><h3>${escapeHtml(title)}</h3>${(block.groups || []).map((group) => `<div class="word-group"><b>${escapeHtml(group.title)}</b><div>${(group.words || block.words || []).map((word) => `<span>${escapeHtml(word)}</span>`).join(' ')}</div></div>`).join('')}${!block.groups ? `<div>${(block.words || []).map((word) => `<span>${escapeHtml(word)}</span>`).join(' ')}</div>` : ''}</section>`;
    case 'year-words':
      return `<section class="block"><h3>${escapeHtml(title)}</h3>${(block.years || []).map((year) => `<div class="word-group"><b>${escapeHtml(year.year)}</b><div>${(year.words || []).map((word) => `<span>${escapeHtml(word)}</span>`).join(' ')}</div></div>`).join('')}</section>`;
    default:
      return `<section class="block"><h3>${escapeHtml(title)}</h3>${renderTextBlock(block.text || block.content || '')}</section>`;
  }
}

function renderConspect(details) {
  const conspect = details?.conspect || {};
  const content = safeJson(details?.contentJson, {});
  const html = content?.html || content?.rawHtml;

  if (content?.mode === 'html' || typeof html === 'string') {
    return `
      <section class="conspect-section">
        <div class="eyebrow">${escapeHtml(conspect.sectionCode || 'Конспект')}</div>
        <h1>${escapeHtml(conspect.title || 'Конспект')}</h1>
        ${conspect.lead ? `<p class="lead">${escapeHtml(conspect.lead)}</p>` : ''}
        <div class="html-conspect">${stripUnsafeHtml(html || '<p>Конспект пока пустой.</p>')}</div>
      </section>`;
  }

  const tabs = Array.isArray(content.tabs) ? content.tabs : [];
  const blocks = tabs.length > 0
    ? tabs.flatMap((tab) => [{ type: 'heading', text: tab.title }, ...(tab.blocks || [])])
    : (content.blocks || []);

  return `
    <section class="conspect-section">
      <div class="eyebrow">${escapeHtml(conspect.sectionCode || 'Конспект')}</div>
      <h1>${escapeHtml(content.hero?.title || conspect.title || 'Конспект')}</h1>
      ${(content.hero?.description || conspect.lead) ? `<p class="lead">${escapeHtml(content.hero?.description || conspect.lead)}</p>` : ''}
      ${blocks.map(renderBlock).join('') || '<p>Конспект пока пустой.</p>'}
    </section>`;
}

function taskIdentity(detailsOrTask) {
  const task = detailsOrTask?.task || detailsOrTask || {};
  return task.id || task.slug || '';
}

function renderTask(details, index) {
  const task = details?.task || details || {};
  const options = optionsFromData(details?.dataJson || task?.dataJson);
  const extra = extraTaskText(details?.dataJson || task?.dataJson);
  const explanation = explanationText(details?.explanationJson || task?.explanationJson);

  return `
    <article class="task-card">
      <div class="task-meta">${escapeHtml(task.sectionCode || '')}${task.type ? ` · ${escapeHtml(task.type)}` : ''}${task.difficulty ? ` · сложность ${escapeHtml(task.difficulty)}` : ''}</div>
      <h3>${index}. ${escapeHtml(task.title || 'Задание')}</h3>
      ${task.prompt ? `<p class="task-prompt">${escapeHtml(task.prompt)}</p>` : ''}
      ${extra ? `<p class="task-extra">${escapeHtml(extra)}</p>` : ''}
      ${options.length ? `<ol class="options">${options.map((option) => `<li>${escapeHtml(option)}</li>`).join('')}</ol>` : ''}
      ${explanation ? `<div class="explanation"><b>Пояснение:</b>${renderTextBlock(explanation)}</div>` : ''}
    </article>`;
}

function renderTaskGroup(title, taskDetails) {
  const tasks = taskDetails || [];
  return `
    <section class="tasks-section">
      <h2>${escapeHtml(title)}</h2>
      ${tasks.length ? tasks.map((task, index) => renderTask(task, index + 1)).join('') : '<p class="muted">Заданий пока нет.</p>'}
    </section>`;
}

function documentHtml({ title, subtitle, sections }) {
  return `<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>${escapeHtml(title)}</title>
  <style>
    @page { size: A4; margin: 16mm 14mm; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #111827; background: #ffffff; font-family: Arial, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; line-height: 1.55; }
    .page { max-width: 980px; margin: 0 auto; padding: 24px; }
    .cover { margin-bottom: 28px; padding: 28px; border: 1px solid #d1d5db; border-radius: 22px; background: #f8fafc; }
    .cover h1 { margin: 0; font-size: 34px; line-height: 1.12; }
    .cover p { margin: 10px 0 0; color: #4b5563; font-size: 15px; }
    .eyebrow, .task-meta { margin-bottom: 8px; color: #0369a1; font-size: 12px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; }
    h1, h2, h3 { color: #111827; page-break-after: avoid; }
    h1 { font-size: 30px; margin: 0 0 12px; }
    h2 { margin: 30px 0 14px; font-size: 24px; border-bottom: 2px solid #e5e7eb; padding-bottom: 7px; }
    h3 { margin: 18px 0 9px; font-size: 18px; }
    p { margin: 7px 0; }
    .lead { font-size: 16px; color: #374151; }
    .conspect-section, .tasks-section { margin-bottom: 28px; }
    .block, .task-card, .example, .word-group { break-inside: avoid; page-break-inside: avoid; }
    .block, .task-card { margin: 12px 0; padding: 14px; border: 1px solid #e5e7eb; border-radius: 16px; background: #ffffff; }
    .task-prompt { font-size: 16px; font-weight: 700; white-space: pre-line; }
    .task-extra { color: #4b5563; white-space: pre-line; }
    .explanation { margin-top: 10px; padding: 10px 12px; border-radius: 12px; background: #f3f4f6; }
    ul, ol { padding-left: 24px; }
    table { border-collapse: collapse; width: 100%; margin: 12px 0; font-size: 13px; }
    th, td { border: 1px solid #d1d5db; padding: 8px; vertical-align: top; text-align: left; }
    th { background: #f3f4f6; }
    img, video { max-width: 100%; }
    .html-conspect { margin-top: 16px; }
    .html-conspect .tf-conspect { max-width: none; }
    .muted { color: #6b7280; }
    .section-break { break-before: page; page-break-before: always; }
    @media print {
      .page { padding: 0; }
      .cover, .block, .task-card { border-color: #d1d5db; }
      a { color: #111827; text-decoration: none; }
    }
  </style>
</head>
<body>
  <main class="page">
    <section class="cover">
      <h1>${escapeHtml(title)}</h1>
      ${subtitle ? `<p>${escapeHtml(subtitle)}</p>` : ''}
    </section>
    ${sections.join('\n')}
  </main>
</body>
</html>`;
}

function openPrintWindow(title) {
  const printWindow = window.open('', '_blank');
  if (!printWindow) {
    throw new Error('Браузер заблокировал окно экспорта. Разреши всплывающие окна для сайта и попробуй ещё раз.');
  }
  printWindow.document.open();
  printWindow.document.write(`<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>${escapeHtml(title)}</title><style>body{font-family:Arial,sans-serif;margin:32px;line-height:1.5}</style></head><body><h1>${escapeHtml(title)}</h1><p>Готовлю материалы для PDF...</p></body></html>`);
  printWindow.document.close();
  return printWindow;
}

function writeAndPrint(printWindow, html) {
  printWindow.document.open();
  printWindow.document.write(html);
  printWindow.document.close();
  printWindow.focus();
  window.setTimeout(() => {
    printWindow.print();
  }, 500);
}

async function mapLimit(items, limit, mapper) {
  const source = [...(items || [])];
  const result = new Array(source.length);
  let index = 0;

  async function worker() {
    while (index < source.length) {
      const current = index;
      index += 1;
      result[current] = await mapper(source[current], current);
    }
  }

  await Promise.all(Array.from({ length: Math.min(limit, source.length) }, worker));
  return result;
}

async function taskDetailsForList(tasks) {
  const loaded = await mapLimit(tasks || [], 4, async (task) => {
    try {
      return await getQuizTask(task.slug || task.id);
    } catch {
      return { task };
    }
  });

  const seen = new Set();
  return loaded.filter((item) => {
    const key = taskIdentity(item);
    if (!key || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

async function loadTasksForConspect(details, includeDraft = false) {
  const conspect = details?.conspect || {};
  const byKey = new Map();

  async function addTaskDetails(items) {
    const loaded = await taskDetailsForList(items || []);
    loaded.forEach((item) => {
      const key = taskIdentity(item);
      if (key) byKey.set(key, item);
    });
  }

  if (conspect.sectionCode) {
    await addTaskDetails(await getQuizTasks({
      subjectCode: conspect.subjectCode || SUBJECT_CODE,
      examCode: conspect.examCode || EXAM_CODE,
      sectionCode: conspect.sectionCode,
      includeDraft,
    }));
  }

  for (const link of details?.taskLinks || []) {
    if (link.taskSlug || link.taskId) {
      try {
        const item = await getQuizTask(link.taskSlug || link.taskId);
        const key = taskIdentity(item);
        if (key) byKey.set(key, item);
      } catch {
        // Если ссылка битая, не ломаем экспорт всего конспекта.
      }
      continue;
    }

    const filter = safeJson(link.taskFilterJson, null);
    if (filter && typeof filter === 'object') {
      await addTaskDetails(await getQuizTasks({ ...filter, includeDraft }));
    }
  }

  return [...byKey.values()];
}

function sectionSort(a, b) {
  const pa = parseSectionCode(a?.sectionCode);
  const pb = parseSectionCode(b?.sectionCode);
  if (!pa && !pb) return 0;
  if (!pa) return 1;
  if (!pb) return -1;
  if (pa.partCode !== pb.partCode) return pa.partCode.localeCompare(pb.partCode);
  return pa.number - pb.number;
}

export async function exportConspectPdf(details, { includeDraft = false } = {}) {
  const conspect = details?.conspect || {};
  const title = `${conspect.sectionCode ? `${conspect.sectionCode}. ` : ''}${conspect.title || 'Конспект'}`;
  const printWindow = openPrintWindow('Экспорт конспекта');

  try {
    const tasks = await loadTasksForConspect(details, includeDraft);
    const html = documentHtml({
      title,
      subtitle: `Конспект${tasks.length ? ` + задания: ${tasks.length}` : ''}`,
      sections: [
        renderConspect(details),
        renderTaskGroup(`Задания${conspect.sectionCode ? ` ${conspect.sectionCode}` : ''}`, tasks),
      ],
    });
    writeAndPrint(printWindow, html);
  } catch (error) {
    printWindow.close();
    throw error;
  }
}

export async function exportSectionPdf({ sectionCode, details, includeDraft = false }) {
  const normalized = normalizeSectionCode(sectionCode || details?.conspect?.sectionCode || '');
  const printWindow = openPrintWindow(`Экспорт ${normalized || 'раздела'}`);

  try {
    let nextDetails = details;
    if (!nextDetails && normalized) {
      const conspects = await getLearningConspects({
        subjectCode: SUBJECT_CODE,
        examCode: EXAM_CODE,
        sectionCode: normalized,
        includeDraft,
      });
      const first = [...(conspects || [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))[0];
      if (first) nextDetails = await getLearningConspect(first.id || first.slug, { includeDraft });
    }

    const tasks = normalized
      ? await taskDetailsForList(await getQuizTasks({ subjectCode: SUBJECT_CODE, examCode: EXAM_CODE, sectionCode: normalized, includeDraft }))
      : [];

    const sections = [];
    if (nextDetails) sections.push(renderConspect(nextDetails));
    sections.push(renderTaskGroup(`Все задания ${normalized}`, tasks));

    writeAndPrint(printWindow, documentHtml({
      title: `${normalized}. Конспект и задания`,
      subtitle: `Выгружено заданий: ${tasks.length}`,
      sections,
    }));
  } catch (error) {
    printWindow.close();
    throw error;
  }
}

export async function exportAllCtPdf(courses, { includeDraft = false } = {}) {
  const printWindow = openPrintWindow('Экспорт частей A и B');

  try {
    const sections = [...(courses || [])]
      .map((course) => ({ ...course, sectionCode: normalizeSectionCode(course.sectionCode) }))
      .filter((course) => course.sectionCode && ['A', 'B'].includes(course.sectionCode[0]))
      .sort(sectionSort);

    const blocks = [];
    let totalTasks = 0;

    for (const course of sections) {
      const code = course.sectionCode;
      const conspects = await getLearningConspects({
        subjectCode: course.subjectCode || SUBJECT_CODE,
        examCode: course.examCode || EXAM_CODE,
        sectionCode: code,
        includeDraft,
      });
      const conspectDetails = await mapLimit(
        [...(conspects || [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0)),
        3,
        (item) => getLearningConspect(item.id || item.slug, { includeDraft }),
      );
      const tasks = await taskDetailsForList(await getQuizTasks({
        subjectCode: course.subjectCode || SUBJECT_CODE,
        examCode: course.examCode || EXAM_CODE,
        sectionCode: code,
        includeDraft,
      }));
      totalTasks += tasks.length;

      blocks.push(`
        <section class="section-break">
          <div class="eyebrow">Раздел ${escapeHtml(code)}</div>
          <h1>${escapeHtml(code)}. ${escapeHtml(course.title || 'Конспект и задания')}</h1>
          ${conspectDetails.length ? conspectDetails.map(renderConspect).join('') : '<p class="muted">Конспектов пока нет.</p>'}
          ${renderTaskGroup(`Все задания ${code}`, tasks)}
        </section>`);
    }

    writeAndPrint(printWindow, documentHtml({
      title: 'ЦТ / ЦЭ: конспекты и задания A+B',
      subtitle: `Разделов: ${sections.length}. Заданий: ${totalTasks}.`,
      sections: blocks.length ? blocks : ['<p class="muted">Созданных разделов A/B пока нет.</p>'],
    }));
  } catch (error) {
    printWindow.close();
    throw error;
  }
}
