import React from 'react';
import { Badge } from '../ui';
import StatementViewer from '../tiptap/StatementViewer';

function renderAnswer(block) {
  const ans = block?.userAnswer || {};
  const kind = String(block?.kind || '').toLowerCase();

  if (kind === 'single-choice' || kind === 'multi-choice') {
    const selected = new Set(Array.isArray(ans.selectedOptionKeys) ? ans.selectedOptionKeys : []);
    const options = Array.isArray(block.options) ? block.options : [];
    return (
      <div className="space-y-2">
        {options.map((o) => {
          const active = selected.has(o.key);
          return (
            <div key={o.key} className={`rounded-xl border px-3 py-2 text-sm ${active ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.08)]' : 'border-neutral-200 dark:border-neutral-800'}`}>
              {o.text}
            </div>
          );
        })}
      </div>
    );
  }

  if (kind === 'number' || kind === 'expression' || kind === 'set') {
    return <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2 text-sm whitespace-pre-wrap">{ans.text || '—'}</div>;
  }

  if (kind === 'order') {
    const items = Array.isArray(ans.orderedItems) ? ans.orderedItems : [];
    return (
      <div className="space-y-2">
        {items.length === 0 ? <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2 text-sm">—</div> : null}
        {items.map((item, idx) => (
          <div key={`${idx}_${item}`} className="flex items-center gap-2 rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2 text-sm">
            <Badge>{idx + 1}</Badge>
            <span>{item}</span>
          </div>
        ))}
      </div>
    );
  }

  if (kind === 'match') {
    const pairs = Array.isArray(ans.matchPairs) ? ans.matchPairs : [];
    const leftMap = new Map((block.matchLeftItems || []).map((x) => [x.key, x.text]));
    const rightMap = new Map((block.matchRightItems || []).map((x) => [x.key, x.text]));
    return (
      <div className="space-y-2">
        {pairs.length === 0 ? <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2 text-sm">—</div> : null}
        {pairs.map((pair, idx) => (
          <div key={`${pair.leftKey}_${pair.rightKey}_${idx}`} className="grid md:grid-cols-2 gap-2 rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2 text-sm">
            <div>{leftMap.get(pair.leftKey) || pair.leftKey}</div>
            <div className="text-neutral-500 dark:text-neutral-300">{rightMap.get(pair.rightKey) || pair.rightKey}</div>
          </div>
        ))}
      </div>
    );
  }

  return <div className="text-sm text-neutral-500 dark:text-neutral-400">Информационный блок</div>;
}

export default function MathAttemptReview({ dto, admin = false }) {
  if (!dto) return null;
  const blocks = Array.isArray(dto.blocks) ? dto.blocks : [];

  return (
    <div className="mt-4 space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge intent={dto.passed ? 'success' : 'danger'}>{dto.passed ? 'Зачёт' : 'Не зачтено'}</Badge>
        <Badge intent="secondary">{dto.scorePercent}% • {dto.earnedScore}/{dto.totalScore}</Badge>
        {dto.timeExpired ? <Badge intent="danger">Время вышло</Badge> : null}
      </div>

      <div className="space-y-4">
        {blocks.map((block, i) => (
          <div key={block.id || i} className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-4 bg-[rgb(var(--card))]">
            <div className="flex items-start justify-between gap-3">
              <div className="font-medium">{i + 1}. {block.prompt || 'Блок'}</div>
              <div className="flex flex-wrap items-center gap-2">
                <Badge intent={block.isCorrect ? 'success' : 'danger'}>{block.isCorrect ? 'Верно' : 'Ошибка'}</Badge>
                <Badge intent="outline">{block.kind}</Badge>
                {Number(block.score) > 0 ? <Badge intent="secondary">{block.score} б.</Badge> : null}
              </div>
            </div>
            {block.promptContentJson ? <div className="mt-3"><StatementViewer value={block.promptContentJson} /></div> : null}
            <div className="mt-3">{renderAnswer(block)}</div>
            {admin && (block.acceptedAnswers?.length || block.correctOptionKeys?.length || block.matchPairs?.length || block.orderItems?.length) ? (
              <div className="mt-3 rounded-xl border border-dashed border-neutral-200 dark:border-neutral-700 p-3 text-sm">
                <div className="font-medium mb-2">Эталон</div>
                {block.acceptedAnswers?.length ? <div>Ответы: {block.acceptedAnswers.join(' | ')}</div> : null}
                {block.correctOptionKeys?.length ? <div>Ключи вариантов: {block.correctOptionKeys.join(', ')}</div> : null}
                {block.orderItems?.length ? <div>Порядок: {block.orderItems.join(' → ')}</div> : null}
                {block.matchPairs?.length ? <div>Пар: {block.matchPairs.map((x) => `${x.leftKey}→${x.rightKey}`).join(', ')}</div> : null}
              </div>
            ) : null}
          </div>
        ))}
      </div>
    </div>
  );
}
