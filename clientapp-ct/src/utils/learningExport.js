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

function renderTaskGroup(title, taskDetails, { renderEmpty = true } = {}) {
  const tasks = taskDetails || [];
  if (!tasks.length && !renderEmpty) return '';

  return `
    <section class="tasks-section">
      <h2>${escapeHtml(title)}</h2>
      ${tasks.length ? tasks.map((task, index) => renderTask(task, index + 1)).join('') : '<p class="muted">Заданий пока нет.</p>'}
    </section>`;
}

const A4_SCREEN_WIDTH_MM = 196;

function exportLayoutOverrideStyle() {
  return `<style id="tf-export-layout-override">
    @page { size: A4 portrait; margin: 6mm; }

    html, body {
      width: auto !important;
      min-width: 0 !important;
      margin: 0 !important;
      padding: 0 !important;
      background: #ffffff !important;
    }

    body {
      color: #111827 !important;
      font-family: Arial, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif !important;
      font-size: 9.2pt !important;
      line-height: 1.28 !important;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }

    .page {
      width: 100% !important;
      max-width: none !important;
      min-width: 0 !important;
      min-height: auto !important;
      margin: 0 auto !important;
      padding: 0 !important;
      background: #ffffff !important;
      box-shadow: none !important;
      overflow: visible !important;
    }

    .print-content {
      width: 100% !important;
      max-width: none !important;
      margin: 0 !important;
      padding: 0 !important;
    }

    .cover {
      margin: 0 0 5mm !important;
      padding: 4.5mm 5mm !important;
      border: .25mm solid #d1d5db !important;
      border-radius: 3.5mm !important;
      background: #f8fafc !important;
    }

    .cover h1 { font-size: 17pt !important; line-height: 1.08 !important; margin: 0 !important; }
    .cover p { margin: 1.5mm 0 0 !important; font-size: 8.5pt !important; color: #4b5563 !important; }

    .eyebrow, .task-meta {
      margin: 0 0 1.5mm !important;
      color: #0369a1 !important;
      font-size: 7.2pt !important;
      font-weight: 800 !important;
      letter-spacing: .06em !important;
      text-transform: uppercase !important;
    }

    h1, h2, h3 { color: #111827 !important; break-after: avoid !important; page-break-after: avoid !important; }
    h1 { font-size: 18pt !important; line-height: 1.12 !important; margin: 0 0 3mm !important; }
    h2 {
      margin: 5mm 0 2.5mm !important;
      padding-bottom: 1.2mm !important;
      font-size: 13pt !important;
      line-height: 1.15 !important;
      border-bottom: .25mm solid #e5e7eb !important;
    }
    h3 { font-size: 10.5pt !important; line-height: 1.18 !important; margin: 3mm 0 1.5mm !important; }
    p { margin: 1.2mm 0 !important; }
    .lead { font-size: 9pt !important; color: #374151 !important; }

    .conspect-section, .tasks-section { margin-bottom: 5mm !important; }
    .block, .task-card, .example, .word-group { break-inside: avoid !important; page-break-inside: avoid !important; }
    .block, .task-card {
      margin: 2.5mm 0 !important;
      padding: 3mm !important;
      border: .25mm solid #e5e7eb !important;
      border-radius: 3mm !important;
      background: #ffffff !important;
    }

    .task-prompt { font-size: 9.5pt !important; font-weight: 700 !important; white-space: pre-line !important; }
    .task-extra { color: #4b5563 !important; white-space: pre-line !important; }
    .explanation { margin-top: 2mm !important; padding: 2mm 2.5mm !important; border-radius: 2.5mm !important; background: #f3f4f6 !important; }
    ul, ol { padding-left: 5mm !important; margin: 1.5mm 0 !important; }
    table { border-collapse: collapse !important; width: 100% !important; margin: 2mm 0 !important; font-size: 8pt !important; table-layout: fixed !important; }
    th, td { border: .25mm solid #d1d5db !important; padding: 1.6mm !important; vertical-align: top !important; text-align: left !important; overflow-wrap: anywhere !important; }
    th { background: #f3f4f6 !important; }

    img, video, canvas, svg { max-width: 100% !important; height: auto !important; }

    .html-conspect {
      width: 100% !important;
      max-width: none !important;
      margin: 0 !important;
      padding: 0 !important;
      overflow: visible !important;
      font-size: 9.2pt !important;
      line-height: 1.28 !important;
    }

    .html-conspect *,
    .html-conspect *::before,
    .html-conspect *::after {
      box-sizing: border-box !important;
      max-width: none !important;
    }

    .html-conspect > div,
    .html-conspect > section,
    .html-conspect > main,
    .html-conspect > article,
    .html-conspect .tf-conspect,
    .html-conspect .tf-conspect > div,
    .html-conspect .tf-conspect > section,
    .html-conspect main,
    .html-conspect article,
    .html-conspect .container,
    .html-conspect .wrapper,
    .html-conspect .content,
    .html-conspect .page,
    .html-conspect .sheet,
    .html-conspect .screen,
    .html-conspect .lesson,
    .html-conspect .lesson-page,
    .html-conspect .conspect,
    .html-conspect .conspect-page {
      width: 100% !important;
      max-width: none !important;
      min-width: 0 !important;
      margin-left: 0 !important;
      margin-right: 0 !important;
    }

    .html-conspect [class*="max-w"],
    .html-conspect [class*="mx-auto"],
    .html-conspect [style*="max-width"] {
      max-width: none !important;
    }

    .html-conspect [class*="mx-auto"] {
      margin-left: 0 !important;
      margin-right: 0 !important;
    }

    .html-conspect [style*="width"] {
      max-width: 100% !important;
    }

    .html-conspect [style*="min-height"] { min-height: auto !important; }
    .html-conspect [style*="height"] { min-height: auto !important; }
    .html-conspect [style*="position: fixed"],
    .html-conspect [style*="position:fixed"] { position: static !important; }
    .html-conspect [style*="transform"] { transform: none !important; }

    .html-conspect [class*="shadow"],
    .html-conspect [style*="box-shadow"] {
      box-shadow: 0 1.5mm 5mm rgba(15, 23, 42, .08) !important;
    }

    .html-conspect h1 { font-size: 18pt !important; line-height: 1.08 !important; margin: 0 0 3mm !important; }
    .html-conspect h2 { font-size: 13pt !important; line-height: 1.13 !important; margin: 5mm 0 2.5mm !important; }
    .html-conspect h3 { font-size: 10.5pt !important; line-height: 1.18 !important; margin: 3mm 0 1.5mm !important; }
    .html-conspect p,
    .html-conspect li,
    .html-conspect td,
    .html-conspect th,
    .html-conspect span {
      font-size: 8.8pt !important;
      line-height: 1.28 !important;
    }

    .html-conspect section,
    .html-conspect article,
    .html-conspect .card,
    .html-conspect [class*="card"],
    .html-conspect [class*="rounded"] {
      border-radius: 3mm !important;
    }

    .html-conspect [class*="p-"],
    .html-conspect [style*="padding"] {
      padding: 3mm !important;
    }

    .html-conspect img,
    .html-conspect video,
    .html-conspect canvas,
    .html-conspect svg {
      max-width: 100% !important;
      height: auto !important;
    }

    .html-conspect [class*="gap"] { gap: 2mm !important; }

    .html-conspect [style*="grid-template-columns"],
    .html-conspect .grid {
      display: grid !important;
      grid-template-columns: repeat(auto-fit, minmax(45mm, 1fr)) !important;
      gap: 2mm !important;
    }

    .html-conspect table { width: 100% !important; table-layout: fixed !important; }
    .html-conspect pre, .html-conspect code { white-space: pre-wrap !important; overflow-wrap: anywhere !important; }

    .muted { color: #6b7280 !important; }
    .section-break { break-before: page !important; page-break-before: always !important; }

    @media screen {
      body { padding: 7mm 0 !important; background: #e5e7eb !important; }
      .page { width: ${A4_SCREEN_WIDTH_MM}mm !important; max-width: ${A4_SCREEN_WIDTH_MM}mm !important; padding: 0 !important; box-shadow: 0 8mm 20mm rgba(15, 23, 42, .16) !important; }
    }

    @media print {
      html, body { background: #ffffff !important; }
      .page { width: 100% !important; max-width: none !important; margin: 0 !important; box-shadow: none !important; }
      a { color: #111827 !important; text-decoration: none !important; }
    }
  </style>`;
}

function documentHtml({ title, subtitle, sections }) {
  const layoutOverride = exportLayoutOverrideStyle();

  return `<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>${escapeHtml(title)}</title>
  ${layoutOverride}
</head>
<body>
  <main class="page">
    <div class="print-content">
      <section class="cover">
        <h1>${escapeHtml(title)}</h1>
        ${subtitle ? `<p>${escapeHtml(subtitle)}</p>` : ''}
      </section>
      ${sections.join('\n')}
    </div>
  </main>
  ${layoutOverride}
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
  }, 800);
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
        renderTaskGroup(`Задания${conspect.sectionCode ? ` ${conspect.sectionCode}` : ''}`, tasks, { renderEmpty: false }),
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
    if (tasks.length) sections.push(renderTaskGroup(`Все задания ${normalized}`, tasks, { renderEmpty: false }));

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
    let exportedSections = 0;

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
      if (!conspectDetails.length && !tasks.length) {
        continue;
      }

      exportedSections += 1;
      totalTasks += tasks.length;

      blocks.push(`
        <section class="section-break">
          <div class="eyebrow">Раздел ${escapeHtml(code)}</div>
          <h1>${escapeHtml(code)}. ${escapeHtml(course.title || 'Конспект и задания')}</h1>
          ${conspectDetails.length ? conspectDetails.map(renderConspect).join('') : '<p class="muted">Конспектов пока нет.</p>'}
          ${renderTaskGroup(`Все задания ${code}`, tasks, { renderEmpty: false })}
        </section>`);
    }

    writeAndPrint(printWindow, documentHtml({
      title: 'ЦТ / ЦЭ: конспекты и задания A+B',
      subtitle: `Разделов: ${exportedSections}. Заданий: ${totalTasks}.`,
      sections: blocks.length ? blocks : ['<p class="muted">Созданных разделов A/B пока нет.</p>'],
    }));
  } catch (error) {
    printWindow.close();
    throw error;
  }
}
