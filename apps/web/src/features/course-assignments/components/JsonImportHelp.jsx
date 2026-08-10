import React from 'react';
import { Bot, Copy, FileJson, GitBranch, ListTree, MapPinned, Sparkles, Unplug } from 'lucide-react';

import { Badge, Button } from '../../../components/ui';
import {
  TASK_GRAPH_COURSE_REF,
  TASK_GRAPH_EXAMPLES,
  TASK_GRAPH_GUIDE_SECTIONS,
  TASK_GRAPH_MEGA_EXAMPLE,
  taskGraphExampleToText,
} from '../courseTaskGraphJson';

export default function JsonImportHelp({ busy = false, onCopyAiPrompt, onCopyExample, onUseExample }) {
  const megaExample = TASK_GRAPH_EXAMPLES.find((example) => example.key === 'mega') || {
    title: 'Полный граф',
    payload: TASK_GRAPH_MEGA_EXAMPLE,
  };

  return (
    <div className="mt-4 space-y-4 rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--muted))]/20 p-4">
      <div className="grid gap-3 md:grid-cols-3">
        <div className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/65 p-3">
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.14em] text-neutral-500"><ListTree size={14} /> Задания</div>
          <div className="mt-2 text-sm"><code>key</code> связывает объекты внутри файла. <code>id</code> используется только для обновления задания.</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/65 p-3">
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.14em] text-neutral-500"><GitBranch size={14} /> Связи</div>
          <div className="mt-2 text-sm"><code>{TASK_GRAPH_COURSE_REF}</code> обозначает открытый курс. Каждая запись <code>from → to</code> становится стрелкой.</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--accent)/0.45)] bg-[rgba(var(--accent)/0.08)] p-3">
          <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.14em] text-neutral-500"><MapPinned size={14} /> Расположение</div>
          <div className="mt-2 text-sm">Координат в JSON нет. Новые ноды раскладывает TaskForge, существующие сохраняют свои позиции.</div>
        </div>
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        <div className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/60 p-4">
          <div className="flex items-center gap-2 font-semibold"><GitBranch size={16} /> Как описывать дерево</div>
          <div className="mt-3 space-y-2 text-sm text-neutral-500">
            <div><code>{'{ "from": "$course", "to": "intro" }'}</code> — вход из курса в первое задание.</div>
            <div>Несколько связей с одинаковым <code>from</code> создают развилку.</div>
            <div>Несколько связей с одинаковым <code>to</code> создают слияние.</div>
            <div>Отсутствие исходящих связей завершает путь.</div>
          </div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/60 p-4">
          <div className="flex items-center gap-2 font-semibold"><Unplug size={16} /> Задание вне карты</div>
          <div className="mt-3 text-sm text-neutral-500">
            Задание без единой связи импортируется, но не размещается. После импорта оно появится в списке «Не на карте» и его можно перетащить на поле.
          </div>
        </div>
      </div>

      <div className="flex flex-col gap-3 rounded-2xl border border-[rgba(var(--accent)/0.5)] bg-[rgba(var(--accent)/0.08)] p-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <div className="flex items-center gap-2 font-semibold"><Bot size={17} /> Промпт для нейросети</div>
          <div className="mt-1 text-sm text-neutral-500">Внутри уже находятся правила, все типы заданий и ответов, ветви, слияния, эффекты стрелок и запрет координат.</div>
        </div>
        <Button variant="outline" onClick={onCopyAiPrompt} disabled={busy}>
          <Copy size={14} /> Копировать промпт
        </Button>
      </div>

      <div className="grid gap-4 xl:grid-cols-[1.1fr_0.9fr]">
        <div className="space-y-3">
          <div className="flex items-center gap-2 font-semibold"><FileJson size={16} /> Полная схема</div>
          {TASK_GRAPH_GUIDE_SECTIONS.map((section) => (
            <div key={section.key} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/60 p-3">
              <div className="font-semibold">{section.title}</div>
              <div className="mt-2 divide-y divide-[rgba(var(--border)/0.55)]">
                {section.items.map((item) => (
                  <div key={`${section.key}:${item.field}`} className="grid gap-1 py-2 sm:grid-cols-[190px_1fr] sm:gap-3">
                    <code className="text-xs font-semibold">{item.field}</code>
                    <div className="text-sm text-neutral-500">{item.text}</div>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>

        <div className="space-y-3">
          <div className="flex items-center gap-2 font-semibold"><Sparkles size={16} /> Готовые примеры</div>
          <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-1">
            {TASK_GRAPH_EXAMPLES.map((example) => (
              <div key={example.key} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/60 p-3">
                <div className="flex items-center justify-between gap-2">
                  <div className="font-semibold">{example.title}</div>
                  <Badge variant="outline">{example.type}</Badge>
                </div>
                <div className="mt-3 flex flex-wrap gap-2">
                  <Button variant="outline" onClick={() => onCopyExample?.(example)} disabled={busy}>
                    <Copy size={14} /> Копировать
                  </Button>
                  <Button variant="outline" onClick={() => onUseExample?.(example)} disabled={busy}>
                    В редактор
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>

      <details className="rounded-2xl border border-[rgba(var(--accent)/0.5)] bg-[rgb(var(--card))]/65 p-4">
        <summary className="cursor-pointer select-none font-semibold">Мега-пример со всеми возможностями</summary>
        <div className="mt-2 text-sm text-neutral-500">
          В примере есть четыре типа заданий, все виды вопросов теста, все math-блоки, публичные и скрытые тест-кейсы, развилки, слияние, скрытие, открытие по одному, сочетание эффектов и задание вне карты.
        </div>
        <div className="mt-3 flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => onCopyExample?.(megaExample)} disabled={busy}><Copy size={14} /> Копировать</Button>
          <Button variant="outline" onClick={() => onUseExample?.(megaExample)} disabled={busy}>В редактор</Button>
        </div>
        <pre className="mt-3 max-h-[640px] overflow-auto whitespace-pre rounded-xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--background))] p-3 text-xs leading-5">
          {taskGraphExampleToText(megaExample)}
        </pre>
      </details>
    </div>
  );
}
