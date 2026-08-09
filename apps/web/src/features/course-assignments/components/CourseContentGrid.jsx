import React from 'react';
import { resolveGridGapDropIntent, isPointerInsideDndItem } from '../../../utils/gridDragDrop';
import CourseContentCard from './CourseContentCard';

export default function CourseContentGrid({
  entries,
  positionByKey,
  canReorderContentItem,
  courseCanEdit,
  sortMode,
  draggedContentKey,
  dragOverContentKey,
  dragOverContentMode,
  dragOverContentEdge,
  setDragOverContentKey,
  setDragOverContentMode,
  setDragOverContentEdge,
  childProgressByCourseId,
  dragApi,
  onContextMenu,
  onDropContent,
}) {
  const resolveGap = (event) => resolveGridGapDropIntent(
    event.currentTarget,
    event.clientX,
    event.clientY,
    '[data-dnd-content-key]',
    draggedContentKey,
    (element) => element.dataset.dndContentKey,
  );

  return (
    <div
      className="auto-fill-grid auto-fill-grid--dense"
      onContextMenu={(event) => {
        if (event.target === event.currentTarget) onContextMenu(event);
      }}
      onDragOver={(event) => {
        if (!draggedContentKey || sortMode !== 'default') return;
        if (isPointerInsideDndItem(event, '[data-dnd-content-key]')) return;
        const intent = resolveGap(event);
        if (!intent) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        setDragOverContentKey(intent.key);
        setDragOverContentMode(intent.mode);
        setDragOverContentEdge(intent.edge);
      }}
      onDrop={(event) => {
        if (!draggedContentKey || sortMode !== 'default') return;
        if (isPointerInsideDndItem(event, '[data-dnd-content-key]')) return;
        const intent = resolveGap(event);
        if (!intent) return;
        event.preventDefault();
        const sourceKey = event.dataTransfer.getData('text/plain') || draggedContentKey;
        onDropContent(intent.key, sourceKey, intent.mode);
      }}
    >
      {entries.map((entry, index) => {
        const itemCanEdit = canReorderContentItem(entry);
        const dropMode = dragOverContentKey === entry.key ? dragOverContentMode : '';
        return (
          <CourseContentCard
            key={entry.key}
            entry={entry}
            position={positionByKey.get(entry.key) ?? index + 1}
            canEdit={itemCanEdit}
            courseCanEdit={courseCanEdit}
            sortMode={sortMode}
            isDragged={draggedContentKey === entry.key}
            dropMode={dropMode}
            dropEdge={dropMode && dropMode !== 'inside' ? dragOverContentEdge : ''}
            progress={entry.kind === 'course' ? childProgressByCourseId[entry.id] : null}
            dragApi={dragApi}
            onContextMenu={onContextMenu}
          />
        );
      })}
    </div>
  );
}
