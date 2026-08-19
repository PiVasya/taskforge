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

function scopeCssSelector(selector, scope) {
  return selector
    .split(',')
    .map((part) => {
      const item = part.trim();
      if (!item || item.startsWith('@')) return item;
      if (item === ':root' || item === 'html' || item === 'body' || item === 'html body' || item === 'html, body') {
        return scope;
      }
      if (item.startsWith(`${scope} `) || item === scope) return item;
      if (item.startsWith(':root')) return item.replace(':root', scope);
      if (item.startsWith('body.')) return `${scope}${item.slice(4)}`;
      if (item.startsWith('body ')) return `${scope} ${item.slice(5)}`;
      if (item.startsWith('html ')) return `${scope} ${item.slice(5)}`;
      return `${scope} ${item}`;
    })
    .join(', ');
}

function scopeStyleText(css, scope = '.html-conspect') {
  return String(css || '').replace(/(^|[{}])([^{}@][^{}]*?)\{/g, (match, prefix, selector) => {
    const trimmed = selector.trim();
    if (!trimmed || trimmed.startsWith('@')) return match;
    return `${prefix}${scopeCssSelector(trimmed, scope)} {`;
  });
}

function prepareHtmlConspectForExport(html) {
  const safe = stripUnsafeHtml(html || '<p>Конспект пока пустой.</p>');
  const styles = [];
  const withoutStyles = safe.replace(/<style\b[^>]*>([\s\S]*?)<\/style>/gi, (_, css) => {
    styles.push(`<style>${scopeStyleText(css)}</style>`);
    return '';
  });
  const bodyMatch = withoutStyles.match(/<body\b[^>]*>([\s\S]*?)<\/body>/i);
  const body = (bodyMatch ? bodyMatch[1] : withoutStyles)
    .replace(/<!doctype[^>]*>/gi, '')
    .replace(/<\/?(?:html|head|body)[^>]*>/gi, '')
    .replace(/<meta\b[^>]*>/gi, '')
    .replace(/<title\b[^<]*(?:(?!<\/title>)<[^<]*)*<\/title>/gi, '');

  return `${styles.join('\n')}\n${body}`;
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
        <div class="html-conspect">${prepareHtmlConspectForExport(html)}</div>
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

function renderTaskGroup(title, taskDetails, { hideEmpty = true } = {}) {
  const tasks = taskDetails || [];
  if (!tasks.length && hideEmpty) return '';
  return `
    <section class="tasks-section">
      <h2>${escapeHtml(title)}</h2>
      ${tasks.length ? tasks.map((task, index) => renderTask(task, index + 1)).join('') : '<p class="muted">Заданий пока нет.</p>'}
    </section>`;
}

const PRINT_PAGE_PADDING = '3mm 4mm 4mm';

function documentHtml({ title, subtitle, sections }) {
  return `<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>${escapeHtml(title)}</title>
  <style>
    @page { size: A4 portrait; margin: 0; }
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; width: 100%; min-height: 100%; }
    body {
      color: #101827;
      background: #e8edf5;
      font-family: Arial, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      font-size: 9.6pt;
      line-height: 1.34;
      -webkit-print-color-adjust: exact;
      print-color-adjust: exact;
      text-rendering: geometricPrecision;
    }
    .tf-export-page {
      width: 210mm;
      max-width: 210mm;
      min-height: 297mm;
      margin: 0 auto;
      padding: ${PRINT_PAGE_PADDING};
      background: #ffffff;
      box-shadow: 0 16px 48px rgba(15, 23, 42, 0.14);
      overflow: visible;
    }
    .print-content { width: 100%; max-width: 100%; }
    .cover {
      margin: 0 0 6mm;
      padding: 5mm 6mm;
      border: .25mm solid #d8e0ea;
      border-radius: 4mm;
      background: linear-gradient(135deg, #f8fbff 0%, #eef6ff 100%);
      break-inside: avoid;
      page-break-inside: avoid;
    }
    .cover h1 { margin: 0; font-size: 17.5pt; line-height: 1.1; letter-spacing: -.03em; }
    .cover p { margin: 2mm 0 0; color: #475569; font-size: 9.2pt; }
    .eyebrow, .task-meta {
      margin: 0 0 2mm;
      color: #0369a1;
      font-size: 7.4pt;
      font-weight: 800;
      letter-spacing: .08em;
      text-transform: uppercase;
    }
    h1, h2, h3 {
      color: #111827;
      break-after: avoid;
      page-break-after: avoid;
      overflow-wrap: anywhere;
      word-break: normal;
    }
    h1 { font-size: 18pt; line-height: 1.08; margin: 0 0 4mm; letter-spacing: -.03em; }
    h2 {
      margin: 6mm 0 3mm;
      font-size: 13.6pt;
      line-height: 1.14;
      border-bottom: .25mm solid #e2e8f0;
      padding-bottom: 1.6mm;
      letter-spacing: -.025em;
    }
    h3 { margin: 4mm 0 2mm; font-size: 10.8pt; line-height: 1.18; }
    p { margin: 1.3mm 0; }
    .lead { font-size: 9.4pt; color: #475569; }
    .conspect-section, .tasks-section { margin-bottom: 6mm; }
    .task-card, .cover { break-inside: avoid; page-break-inside: avoid; }
    .block, .task-card {
      margin: 3mm 0;
      padding: 3.2mm 3.8mm;
      border: .25mm solid #e2e8f0;
      border-radius: 3.5mm;
      background: #ffffff;
    }
    .task-card { background: #fbfdff; }
    .task-prompt { font-size: 10pt; font-weight: 700; white-space: pre-line; }
    .task-extra { color: #475569; white-space: pre-line; }
    .explanation { margin-top: 2mm; padding: 2.2mm 3mm; border-radius: 3mm; background: #f1f5f9; }
    ul, ol { padding-left: 5.5mm; margin: 2mm 0; }
    li { margin: .6mm 0; }
    table {
      border-collapse: collapse;
      width: 100%;
      margin: 3mm 0;
      font-size: 8.5pt;
      table-layout: fixed;
    }
    th, td {
      border: .25mm solid #d1d5db;
      padding: 1.8mm;
      vertical-align: top;
      text-align: left;
      overflow-wrap: anywhere;
    }
    th { background: #f1f5f9; }
    img, video, canvas, svg { max-width: 100% !important; height: auto !important; }

    .html-conspect {
      margin-top: 4mm;
      width: 100%;
      max-width: 100%;
      overflow: visible;
      color: #111827;
      font-size: 8.9pt !important;
      line-height: 1.3 !important;
      word-spacing: .08em !important;
    }
    .html-conspect, .html-conspect * {
      box-sizing: border-box !important;
      max-width: 100% !important;
      text-rendering: geometricPrecision !important;
    }
    body.tf-print-export .html-conspect {
      margin: 2mm 0 0 !important;
      width: 100% !important;
      max-width: none !important;
      overflow: visible !important;
      background: #ffffff !important;
    }
    body.tf-print-export .html-conspect .page {
      width: 100% !important;
      max-width: none !important;
      min-height: auto !important;
      margin: 0 !important;
      padding: 0 !important;
      background: #ffffff !important;
    }
    body.tf-print-export .html-conspect .sheet {
      width: 100% !important;
      max-width: none !important;
      margin: 0 !important;
      border: 0 !important;
      border-radius: 0 !important;
      box-shadow: none !important;
      overflow: visible !important;
      background: #ffffff !important;
    }
    body.tf-print-export .html-conspect .hero {
      padding: 7mm 8mm 6mm !important;
      border-radius: 0 !important;
    }
    body.tf-print-export .html-conspect .mini-map {
      padding: 4mm 8mm !important;
      gap: 2mm !important;
    }
    body.tf-print-export .html-conspect section {
      padding: 5mm 8mm !important;
    }
    body.tf-print-export .html-conspect .footer {
      padding: 4mm 8mm !important;
    }
    body.tf-print-export .html-conspect .brand { margin-bottom: 5mm !important; }
    body.tf-print-export .html-conspect .brand-mark {
      width: 13mm !important;
      height: 13mm !important;
      border-radius: 3mm !important;
      font-size: 15pt !important;
    }
    body.tf-print-export .html-conspect .brand-title { font-size: 10.5pt !important; }
    body.tf-print-export .html-conspect .brand-sub { font-size: 7.5pt !important; }
    body.tf-print-export .html-conspect .hero-text {
      max-width: none !important;
      font-size: 8.8pt !important;
      margin-top: 2mm !important;
    }
    body.tf-print-export .html-conspect .flower {
      right: 7mm !important;
      bottom: 6mm !important;
      transform: scale(.72) !important;
      transform-origin: center !important;
    }
    body.tf-print-export .html-conspect .mini-map { grid-template-columns: repeat(4, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .steps { grid-template-columns: repeat(3, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .root-list { grid-template-columns: repeat(3, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .exception-grid { grid-template-columns: repeat(3, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .check-line { grid-template-columns: repeat(4, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .dictionary-grid { grid-template-columns: repeat(2, minmax(0, 1fr)) !important; }
    body.tf-print-export .html-conspect .word-list { columns: 2 !important; column-gap: 3mm !important; }
    body.tf-print-export .html-conspect .letter-card { min-height: auto !important; }
    .html-conspect .tf-conspect,
    .html-conspect main,
    .html-conspect section,
    .html-conspect article {
      width: 100% !important;
      max-width: 100% !important;
      margin-left: 0 !important;
      margin-right: 0 !important;
      padding-left: 0 !important;
      padding-right: 0 !important;
    }
    .html-conspect [class*="max-w-"],
    .html-conspect [style*="max-width"],
    .html-conspect [style*="width: min"],
    .html-conspect [style*="width:min"] {
      max-width: 100% !important;
      width: 100% !important;
    }
    .html-conspect [class*="mx-auto"],
    .html-conspect [style*="margin: 0 auto"],
    .html-conspect [style*="margin:0 auto"] {
      margin-left: 0 !important;
      margin-right: 0 !important;
    }
    .html-conspect [style*="min-height"] { min-height: auto !important; }
    .html-conspect [style*="height"] { min-height: auto !important; }
    .html-conspect [style*="position: fixed"],
    .html-conspect [style*="position:fixed"] { position: static !important; }
    .html-conspect [style*="transform"] { transform: none !important; }
    .html-conspect [class*="shadow"],
    .html-conspect [style*="box-shadow"] {
      box-shadow: 0 3mm 9mm rgba(15, 23, 42, .08) !important;
    }
    .html-conspect [class*="rounded"],
    .html-conspect [style*="border-radius"] { border-radius: 3.2mm !important; }
    .html-conspect [class*="p-"],
    .html-conspect [style*="padding"] { }
    .html-conspect h1 {
      font-size: 18pt !important;
      line-height: 1.08 !important;
      margin: 0 0 3.4mm !important;
      letter-spacing: -.035em !important;
      word-spacing: .1em !important;
    }
    .html-conspect h2 {
      font-size: 13.2pt !important;
      line-height: 1.12 !important;
      margin: 5.5mm 0 2.7mm !important;
      letter-spacing: -.025em !important;
      word-spacing: .09em !important;
    }
    .html-conspect h3,
    .html-conspect h4 {
      font-size: 10.4pt !important;
      line-height: 1.16 !important;
      margin: 3.4mm 0 1.8mm !important;
      word-spacing: .08em !important;
    }
    .html-conspect p,
    .html-conspect li,
    .html-conspect td,
    .html-conspect th,
    .html-conspect span,
    .html-conspect small {
      font-size: 8.7pt !important;
      line-height: 1.28 !important;
      word-spacing: .08em !important;
      white-space: normal !important;
    }
    .html-conspect ul,
    .html-conspect ol { margin: 1.6mm 0 !important; padding-left: 5mm !important; }
    .html-conspect table { font-size: 8.2pt !important; margin: 2.4mm 0 !important; }
    .html-conspect th,
    .html-conspect td { padding: 1.5mm !important; }
    .html-conspect div,
    .html-conspect section,
    .html-conspect article {
      break-inside: auto;
      page-break-inside: auto;
    }
    .html-conspect .card,
    .html-conspect [class*="card"],
    .html-conspect [class*="rounded"] {
      break-inside: avoid;
      page-break-inside: avoid;
    }
    .html-conspect .card,
    .html-conspect [class*="card"] {
      padding: 3mm !important;
      margin: 2.2mm 0 !important;
    }
    .html-conspect [class*="gap-"] { gap: 2mm !important; }
    .html-conspect [class*="grid"] {
      column-gap: 2.6mm !important;
      row-gap: 2.4mm !important;
    }
    .html-conspect [class*="text-4xl"],
    .html-conspect [class*="text-5xl"],
    .html-conspect [class*="text-6xl"] { font-size: 18pt !important; }
    .html-conspect [class*="text-3xl"] { font-size: 15pt !important; }
    .html-conspect [class*="text-2xl"] { font-size: 12.5pt !important; }
    .html-conspect [class*="text-xl"] { font-size: 10.5pt !important; }
    .html-conspect [class*="text-lg"] { font-size: 9.4pt !important; }
    .html-conspect .export-inline-gap > * + *::before { content: " "; }

    .muted { color: #64748b; }
    .section-break { break-before: page; page-break-before: always; }

    @media screen {
      body.tf-print-export { padding: 6mm 0; }
    }

    @media print {
      html, body { width: 210mm; min-height: auto; background: #ffffff; }
      body.tf-print-export { font-size: 9.3pt; background: #ffffff !important; }
      .tf-export-page {
        width: 210mm;
        max-width: 210mm;
        min-height: 297mm;
        margin: 0;
        padding: ${PRINT_PAGE_PADDING};
        box-shadow: none;
      }
      .print-content { padding: 0; }
      body.tf-print-export .html-conspect .page,
      body.tf-print-export .html-conspect .sheet { width: 100% !important; max-width: none !important; }
      body.tf-print-export .html-conspect section { padding-top: 4.5mm !important; padding-bottom: 4.5mm !important; }
      .cover, .block, .task-card { border-color: #d7dee8; }
      .section-break:first-of-type { break-before: auto; page-break-before: auto; }
      a { color: #111827; text-decoration: none; }
    }
  </style>
</head>
<body class="tf-print-export">
  <main class="tf-export-page">
    <div class="print-content">
      <section class="cover">
        <h1>${escapeHtml(title)}</h1>
        ${subtitle ? `<p>${escapeHtml(subtitle)}</p>` : ''}
      </section>
      ${sections.filter(Boolean).join('\n')}
    </div>
  </main>
  <script>
    (function () {
      function isWordChar(ch) {
        return /[A-Za-zА-Яа-яЁё0-9»)]/.test(ch || '');
      }
      function startsWord(ch) {
        return /[A-Za-zА-Яа-яЁё0-9«(]/.test(ch || '');
      }
      function tailText(node) {
        if (!node) return '';
        var text = node.textContent || '';
        return text.trim().slice(-1);
      }
      function headText(node) {
        if (!node) return '';
        var text = node.textContent || '';
        return text.trim().charAt(0);
      }
      function addSpacesBetweenInlineChildren() {
        var selector = 'h1,h2,h3,h4,p,li,a,button,label,span,strong,b,em,small';
        document.querySelectorAll(selector).forEach(function (el) {
          var nodes = Array.prototype.slice.call(el.childNodes);
          for (var i = nodes.length - 1; i > 0; i -= 1) {
            var prev = nodes[i - 1];
            var next = nodes[i];
            if (!prev || !next) continue;
            if (prev.nodeType === 3 && /\\s$/.test(prev.nodeValue || '')) continue;
            if (next.nodeType === 3 && /^\\s/.test(next.nodeValue || '')) continue;
            if (isWordChar(tailText(prev)) && startsWord(headText(next))) {
              el.insertBefore(document.createTextNode(' '), next);
            }
          }
        });
      }
      function fixCommonJoinedRussianHeadings() {
        var replacements = [
          ['конспектыизадания', 'конспекты и задания'],
          ['Порядокрешения', 'Порядок решения'],
          ['Быстроеправило', 'Быстрое правило'],
          ['Главноеразличие', 'Главное различие'],
          ['Корнисчередующимисягласными', 'Корни с чередующимися гласными'],
          ['Проверяемыегласные', 'Проверяемые гласные'],
          ['Словарныеслова', 'Словарные слова'],
          ['Запомнитьзначения', 'Запомнить значения'],
          ['Запомнитьгласную', 'Запомнить гласную'],
          ['Какопределитьспряжение', 'Как определить спряжение'],
          ['Причастиянастоящеговремени', 'Причастия настоящего времени'],
          ['Опасныеинфинитивы', 'Опасные инфинитивы'],
          ['КогдапишемНЕ', 'Когда пишем НЕ'],
          ['КогдапишемНИ', 'Когда пишем НИ'],
          ['ФразеологизмыиустойчивыевыражениясНИ', 'Фразеологизмы и устойчивые выражения с НИ'],
          ['НЕснаречиями', 'НЕ с наречиями'],
          ['НЕссуществительнымииприлагательными', 'НЕ с существительными и прилагательными'],
          ['НЕскраткимиприлагательными', 'НЕ с краткими прилагательными'],
          ['НЕспричастиями', 'НЕ с причастиями'],
          ['НЕсместоимениями', 'НЕ с местоимениями'],
          ['НЕвсегдараздельно', 'НЕ всегда раздельно'],
          ['Первыечастислова', 'Первые части слова'],
          ['Вашесловоприлагательное', 'Ваше слово прилагательное'],
          ['Дефис: еслиперваячастьслова', 'Дефис: если первая часть слова'],
          ['Слитноираздельно', 'Слитно и раздельно'],
          ['Вкоторыхможноошибиться', 'В которых можно ошибиться'],
          ['Зависитотзначения', 'Зависит от значения'],
          ['еслислованебыловсписках', 'если слова не было в списках'],
          ['Запомнитьслитно', 'Запомнить слитно'],
          ['Запомнитьраздельно', 'Запомнить раздельно'],
          ['Быстраясхема', 'Быстрая схема'],
          ['Тиреставится', 'Тире ставится'],
          ['Тиренеставится', 'Тире не ставится'],
          ['Короткаяпроверка', 'Короткая проверка'],
          ['Двадеепричастныхоборота', 'Два деепричастных оборота'],
          ['Выделяемзапятымивпредложении', 'Выделяем запятыми в предложении'],
          ['Выделяемзапятыми', 'Выделяем запятыми'],
          ['Вводноеилиневводное', 'Вводное или невводное'],
          ['Неявляютсявводными', 'Не являются вводными'],
          ['Особыеслучаи', 'Особые случаи'],
          ['Запятаяставится', 'Запятая ставится'],
          ['Запятаянеставится', 'Запятая не ставится'],
          ['Запятаямеждучастямисложногопредложения', 'Запятая между частями сложного предложения'],
          ['Когдавсложномпредложениизапятаяненужна', 'Когда в сложном предложении запятая не нужна'],
          ['ЗапятаявССП', 'Запятая в ССП'],
          ['ЗапятаявССПнеставится', 'Запятая в ССП не ставится'],
          ['ЗапятаявСПП', 'Запятая в СПП'],
          ['ЗапятаявСППнеставится', 'Запятая в СПП не ставится'],
          ['Правилолюбовницы', 'Правило любовницы'],
          ['Двоеточиеставится', 'Двоеточие ставится'],
          ['Тиреставится', 'Тире ставится'],
          ['Основныесхемы', 'Основные схемы'],
          ['Случаисзапятой', 'Случаи с запятой'],
          ['Случаибеззапятой', 'Случаи без запятой'],
          ['Разговорныйиписьменный', 'Разговорный и письменный'],
          ['Письменныестили', 'Письменные стили'],
          ['Основныеспособысвязи', 'Основные способы связи'],
          ['Местоимениякаксредствосвязи', 'Местоимения как средство связи'],
          ['Союзы, наречияипорядокслов', 'Союзы, наречия и порядок слов'],
          ['Словапозначению', 'Слова по значению'],
          ['Контекстныеантонимыисинонимы', 'Контекстные антонимы и синонимы'],
          ['Однозначныеимногозначныеслова', 'Однозначные и многозначные слова'],
          ['БуквыЯ, Е, Ё, Юобозначают', 'Буквы Я, Е, Ё, Ю обозначают'],
          ['Звонкиеиглухиесогласные', 'Звонкие и глухие согласные'],
          ['Всегдамягкиеивсегдатвёрдые', 'Всегда мягкие и всегда твёрдые'],
          ['Оглушениеиозвончение', 'Оглушение и озвончение'],
          ['Группыдлязапоминания', 'Группы для запоминания'],
          ['Видысвязивсловосочетаниях', 'Виды связи в словосочетаниях'],
          ['Несловосочетания', 'Не словосочетания'],
          ['Типыречевыхошибок', 'Типы речевых ошибок'],
          ['Морфологическиенормы', 'Морфологические нормы'],
          ['Степенисравнения', 'Степени сравнения'],
          ['Формымножественногочисла', 'Формы множественного числа'],
          ['Множественноечисло', 'Множественное число'],
          ['Родительныйпадежмножественногочисла', 'Родительный падеж множественного числа'],
          ['Настоящее, будущееипрошедшеевремя', 'Настоящее, будущее и прошедшее время'],
          ['гласнаявкорнеслова', 'гласная в корне слова'],
          ['гласнаявкорнеиприставкипре/при', 'гласная в корне и приставки пре/при'],
          ['Е/Ивкорнеипре/при', 'Е/И в корне и пре/при'],
          ['причастияиопасныеинфинитивы', 'причастия и опасные инфинитивы'],
          ['НЕиНИ', 'НЕ и НИ'],
          ['НЕсразнымичастямиречи', 'НЕ с разными частями речи'],
          ['тиремеждуподлежащимисказуемым', 'тире между подлежащим и сказуемым'],
          ['обособленныеобстоятельстваи', 'обособленные обстоятельства и'],
          ['вводныеилжевводныеслова', 'вводные и лжевводные слова'],
          ['запятаямеждуоднороднымиичастями', 'запятая между однородными и частями'],
          ['сложногопредложения', 'сложного предложения'],
          ['запятаявССПиСПП', 'запятая в ССП и СПП'],
          ['знакивбессоюзномсложномпредложении', 'знаки в бессоюзном сложном предложении'],
          ['прямаяречь, цитатыидиалог', 'прямая речь, цитаты и диалог'],
          ['стилиитипыречи', 'стили и типы речи'],
          ['средствасвязипредложенийвтексте', 'средства связи предложений в тексте'],
          ['антонимы, синонимыизначенияслов', 'антонимы, синонимы и значения слов'],
          ['фонетикаизвуки', 'фонетика и звуки'],
          ['разрядыместоимений', 'разряды местоимений'],
          ['речевыенормы', 'речевые нормы'],
          ['синтаксическиеошибки', 'синтаксические ошибки'],
          ['морфологическиенормы', 'морфологические нормы'],
          ['Всезадания', 'Все задания']
        ];
        var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        var nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        nodes.forEach(function (node) {
          var value = node.nodeValue;
          replacements.forEach(function (pair) {
            value = value.split(pair[0]).join(pair[1]);
          });
          value = value
            .replace(/([А-ЯЁA-Z0-9])\\.([А-ЯЁA-Z0-9])/g, '$1. $2')
            .replace(/([а-яё])([A-ZА-ЯЁ][a-zа-яё])/g, '$1 $2')
            .replace(/(A\\d+|B\\d+):\\s*([^\\s]{4,})/g, function (match, code, rest) {
              return code + ': ' + rest;
            });
          node.nodeValue = value;
        });
      }
      addSpacesBetweenInlineChildren();
      fixCommonJoinedRussianHeadings();
    })();
  </script>
</body>
</html>`;
}

function openPrintWindow(title) {
  const printWindow = window.open('', '_blank');
  if (!printWindow) {
    throw new Error('Браузер заблокировал окно экспорта. Разреши всплывающие окна для сайта и попробуй ещё раз.');
  }
  printWindow.opener = null;
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
  }, 1200);
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

      if (!conspectDetails.length && !tasks.length) {
        continue;
      }

      blocks.push(`
        <section class="section-break">
          <div class="eyebrow">Раздел ${escapeHtml(code)}</div>
          <h1>${escapeHtml(code)}. ${escapeHtml(course.title || code)}</h1>
          ${conspectDetails.map(renderConspect).join('')}
          ${renderTaskGroup(`Все задания ${code}`, tasks)}
        </section>`);
    }

    writeAndPrint(printWindow, documentHtml({
      title: 'ЦТ / ЦЭ: конспекты и задания A+B',
      subtitle: `Разделов: ${blocks.length}. Заданий: ${totalTasks}.`,
      sections: blocks.length ? blocks : ['<p class="muted">Созданных разделов A/B пока нет.</p>'],
    }));
  } catch (error) {
    printWindow.close();
    throw error;
  }
}
