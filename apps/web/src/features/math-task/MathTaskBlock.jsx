import React, { useCallback } from 'react';
import { Badge, Button, Card, Field, Select, Textarea } from '../../components/ui';
import StatementViewer from '../../components/tiptap/StatementViewer';
import { setAttemptAnswer, useAttemptAnswer } from '../attempts/attemptAnswerStore';

function MathTaskBlock({ storeKey, block, index }) {
  const answer = useAttemptAnswer(storeKey, block.id);
  const kind = String(block.kind || '').toLowerCase();

  const moveOrderItem = useCallback((itemIndex, direction) => {
    setAttemptAnswer(storeKey, block.id, (current) => {
      const orderedItems = current.orderedItems?.length ? current.orderedItems : (block.orderItems || []);
      const targetIndex = itemIndex + direction;
      if (targetIndex < 0 || targetIndex >= orderedItems.length) return current;
      const next = [...orderedItems];
      [next[itemIndex], next[targetIndex]] = [next[targetIndex], next[itemIndex]];
      return { ...current, orderedItems: next };
    });
  }, [block.id, block.orderItems, storeKey]);

  let body;
  if (kind === 'info') {
    body = <div className="text-sm text-neutral-500">Это информационный блок. Он не оценивается, но помогает провести решение по шагам.</div>;
  } else if (kind === 'single-choice' || kind === 'multi-choice') {
    const selected = Array.isArray(answer.selectedOptionKeys) ? answer.selectedOptionKeys : [];
    const multiple = kind === 'multi-choice';
    body = (
      <div className="space-y-2">
        {(block.options || []).map((option) => (
          <label key={option.key} className="flex items-center gap-2 text-sm cursor-pointer">
            <input
              type={multiple ? 'checkbox' : 'radio'}
              name={`math-${block.id}`}
              checked={selected.includes(option.key)}
              onChange={() => setAttemptAnswer(storeKey, block.id, (current) => {
                if (!multiple) return { ...current, selectedOptionKeys: [option.key] };
                const next = new Set(current.selectedOptionKeys || []);
                if (next.has(option.key)) next.delete(option.key);
                else next.add(option.key);
                return { ...current, selectedOptionKeys: Array.from(next) };
              })}
            />
            <span>{option.text}</span>
          </label>
        ))}
      </div>
    );
  } else if (kind === 'number' || kind === 'expression' || kind === 'set') {
    body = (
      <Field label={kind === 'number' ? 'Ответ' : kind === 'set' ? 'Множество / список' : 'Формула / выражение'}>
        <Textarea
          rows={kind === 'expression' ? 3 : 2}
          value={answer.text || ''}
          onChange={(event) => setAttemptAnswer(storeKey, block.id, (current) => ({ ...current, text: event.target.value }))}
          placeholder={kind === 'number' ? 'Например: 3.14' : kind === 'set' ? 'Например: 1, 2, 3' : 'Например: (x-1)(x+1)'}
        />
      </Field>
    );
  } else if (kind === 'order') {
    const orderedItems = answer.orderedItems?.length ? answer.orderedItems : (block.orderItems || []);
    body = (
      <div className="space-y-2">
        {orderedItems.map((item, itemIndex) => (
          <div key={`${item}_${itemIndex}`} className="flex items-center gap-2 rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2">
            <Badge>{itemIndex + 1}</Badge>
            <div className="flex-1">{item}</div>
            <Button variant="outline" onClick={() => moveOrderItem(itemIndex, -1)}>↑</Button>
            <Button variant="outline" onClick={() => moveOrderItem(itemIndex, 1)}>↓</Button>
          </div>
        ))}
      </div>
    );
  } else if (kind === 'match') {
    const pairs = answer.matchPairs ?? (block.matchLeftItems || []).map((item) => ({ leftKey: item.key, rightKey: '' }));
    body = (
      <div className="space-y-3">
        {(block.matchLeftItems || []).map((left) => (
          <div key={left.key} className="grid md:grid-cols-[1fr_220px] gap-3 items-center">
            <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 px-3 py-2">{left.text}</div>
            <Select
              value={pairs.find((pair) => pair.leftKey === left.key)?.rightKey || ''}
              onChange={(event) => setAttemptAnswer(storeKey, block.id, (current) => {
                const currentPairs = current.matchPairs ?? (block.matchLeftItems || []).map((item) => ({ leftKey: item.key, rightKey: '' }));
                const next = currentPairs.map((pair) => (
                  pair.leftKey === left.key ? { ...pair, rightKey: event.target.value } : pair
                ));
                return { ...current, matchPairs: next };
              })}
            >
              <option value="">— выбери —</option>
              {(block.matchRightItems || []).map((right) => <option key={right.key} value={right.key}>{right.text}</option>)}
            </Select>
          </div>
        ))}
      </div>
    );
  } else {
    body = <div className="text-sm text-rose-500">Неизвестный тип блока: {kind}</div>;
  }

  return (
    <Card>
      <div className="space-y-4">
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="font-medium">{index + 1}. {block.prompt || 'Блок'}</div>
            {block.promptContentJson ? <div className="mt-2"><StatementViewer value={block.promptContentJson} /></div> : null}
          </div>
          <div className="flex items-center gap-2">
            <Badge variant="outline">{block.kind}</Badge>
            {block.score > 0 ? <Badge>{block.score} б.</Badge> : null}
          </div>
        </div>
        {body}
      </div>
    </Card>
  );
}

export default React.memo(MathTaskBlock);
