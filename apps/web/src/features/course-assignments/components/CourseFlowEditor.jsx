import React from 'react';
import { FileCode2, FolderTree, Layers3, Plus, ArrowUpFromLine } from 'lucide-react';
import { previewAssignmentDescription, previewAssignmentTitle, isAssignmentSolved } from '../courseAssignmentsModel';

function FlowConnector({ draggedContentKey, targetKey, mode, onDropContent }) {
  const [active, setActive] = React.useState(false);
  const enabled = Boolean(draggedContentKey && targetKey && onDropContent);

  React.useEffect(() => {
    if (!draggedContentKey) setActive(false);
  }, [draggedContentKey]);

  return (
    <div
      className={`course-flow-connector${active ? ' is-drop-active' : ''}`}
      aria-hidden="true"
      onDragEnter={(event) => {
        if (!enabled) return;
        event.preventDefault();
        setActive(true);
      }}
      onDragOver={(event) => {
        if (!enabled) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        setActive(true);
      }}
      onDragLeave={(event) => {
        if (event.currentTarget?.contains?.(event.relatedTarget)) return;
        setActive(false);
      }}
      onDrop={(event) => {
        if (!enabled) return;
        event.preventDefault();
        event.stopPropagation();
        const sourceKey = event.dataTransfer.getData('text/plain') || draggedContentKey;
        setActive(false);
        onDropContent(targetKey, sourceKey, mode);
      }}
    >
      <span className="course-flow-connector-line" />
      <span className="course-flow-connector-arrow">›</span>
      {active ? <span className="course-flow-connector-drop-label">сюда</span> : null}
    </div>
  );
}

function FlowNode({
  entry,
  position,
  progress,
  canEdit,
  courseCanEdit,
  draggedContentKey,
  dragOverContentKey,
  dragOverContentMode,
  dragApi,
  onContextMenu,
}) {
  const isCourse = entry.kind === 'course';
  const sourceKey = draggedContentKey;
  const isDragging = sourceKey === entry.key;
  const isDropTarget = dragOverContentKey === entry.key && sourceKey && sourceKey !== entry.key;
  const dropMode = isDropTarget ? dragOverContentMode : '';
  const item = isCourse ? entry.course : entry.assignment;
  const title = isCourse
    ? (item.title || `Курс ${position}`)
    : previewAssignmentTitle(item.title, `Задание ${position}`);
  const description = isCourse
    ? (item.description || 'Вложенный курс')
    : previewAssignmentDescription(item.description || '');
  const solved = !isCourse && isAssignmentSolved(item);
  const viewHref = isCourse ? `/course/${item.id}` : `/assignment/${item.id}`;
  const editorHref = isCourse
    ? (courseCanEdit && item.canEdit !== false ? `/courses/${item.id}/edit` : viewHref)
    : `/assignment/${item.id}/edit`;
  const draggable = canEdit;

  const dragProps = draggable ? {
    draggable: true,
    onDragStart: (event) => dragApi.onStart(event, entry),
    onDragEnter: (event) => dragApi.onEnter(event, entry),
    onDragOver: (event) => dragApi.onOver(event, entry),
    onDragLeave: (event) => dragApi.onLeave(event, entry),
    onDrop: (event) => dragApi.onDrop(event, entry),
    onDragEnd: dragApi.onEnd,
  } : {};

  return (
    <div
      className={[
        'course-flow-node',
        isCourse ? 'course-flow-node--course' : 'course-flow-node--assignment',
        solved ? 'course-flow-node--solved' : '',
        isDragging ? 'course-flow-node--dragging' : '',
        dropMode ? `course-flow-node--drop-${dropMode}` : '',
        draggable ? 'course-flow-node--draggable' : '',
      ].filter(Boolean).join(' ')}
      data-dnd-content-key={entry.key}
      data-course-flow-node="true"
      role="link"
      tabIndex={0}
      {...dragProps}
      onContextMenu={(event) => onContextMenu?.(event, entry, canEdit)}
      onClick={() => dragApi.openAfterDrag(editorHref)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          dragApi.openAfterDrag(editorHref);
        }
      }}
      title={draggable ? 'Край узла — переставить. Курс в центр другого курса — вложить.' : 'Открыть'}
    >
      <span className="course-flow-handle course-flow-handle--input" aria-hidden="true" />
      <span className="course-flow-handle course-flow-handle--output" aria-hidden="true" />

      <div className="course-flow-node-heading">
        <div className="course-flow-node-icon" aria-hidden="true">
          {isCourse ? <FolderTree size={18} /> : <FileCode2 size={18} />}
        </div>
        <div className="min-w-0 flex-1">
          <div className="course-flow-node-kicker">{isCourse ? 'Курс' : 'Задание'} · {position}</div>
          <div className="course-flow-node-title" title={title}>{title}</div>
        </div>
      </div>

      {description ? <div className="course-flow-node-description">{description}</div> : null}

      {isCourse ? (
        <div className="course-flow-node-progress">
          <span>{progress?.loading ? '—/—' : `${progress?.solved ?? 0}/${progress?.total ?? 0}`}</span>
          <span className="course-flow-node-progress-track">
            <span style={{ width: `${progress?.total > 0 ? progress.percent : 0}%` }} />
          </span>
        </div>
      ) : (
        <div className="course-flow-node-meta">
          <span>{item.type || item.assignmentType || 'task'}</span>
          {solved ? <span>решено</span> : null}
        </div>
      )}

      {isDropTarget ? (
        <div className="course-flow-drop-guide" aria-hidden="true">
          <span className={dropMode === 'before' ? 'is-active' : ''}>Перед</span>
          {isCourse ? <span className={dropMode === 'inside' ? 'is-active' : ''}>Вложить</span> : <span />}
          <span className={dropMode === 'after' ? 'is-active' : ''}>После</span>
        </div>
      ) : null}
    </div>
  );
}

export default function CourseFlowEditor({
  course,
  entries,
  positionByKey,
  childProgressByCourseId,
  canReorderContentItem,
  courseCanEdit,
  draggedContentKey,
  dragOverContentKey,
  dragOverContentMode,
  dragApi,
  onContextMenu,
  onCreate,
  onDropContent,
  extractDropActive,
  onExtractDragOver,
  onExtractDragLeave,
  onExtractDrop,
}) {
  const source = entries.find((entry) => entry.key === draggedContentKey);
  const draggingCourse = source?.kind === 'course';

  return (
    <div className="course-flow-shell" onContextMenu={(event) => {
      if (event.target === event.currentTarget) onContextMenu?.(event);
    }}>
      <div className="course-flow-help">
        <div>
          <strong>Схема курса.</strong> Тяни узел за саму карточку: край другого узла меняет порядок, центр курса вкладывает курс внутрь.
        </div>
        <div className="course-flow-help-legend" aria-hidden="true">
          <span><i className="course-flow-legend-dot course-flow-legend-dot--task" /> задание</span>
          <span><i className="course-flow-legend-dot course-flow-legend-dot--course" /> курс</span>
        </div>
      </div>

      {course?.parentCourseId && draggingCourse ? (
        <div
          className={`course-flow-extract-zone${extractDropActive ? ' is-active' : ''}`}
          onDragOver={onExtractDragOver}
          onDragLeave={onExtractDragLeave}
          onDrop={onExtractDrop}
        >
          <ArrowUpFromLine size={17} />
          Вынести курс на уровень выше
        </div>
      ) : null}

      <div className="course-flow-viewport">
        <div className="course-flow-track">
          <div className="course-flow-root-node">
            <div className="course-flow-root-icon"><Layers3 size={20} /></div>
            <div className="min-w-0">
              <div className="course-flow-node-kicker">Текущий курс</div>
              <div className="course-flow-root-title">{course?.title || 'Курс'}</div>
            </div>
          </div>

          {entries.map((entry, index) => (
            <React.Fragment key={entry.key}>
              <FlowConnector
                draggedContentKey={draggedContentKey}
                targetKey={entry.key}
                mode="before"
                onDropContent={onDropContent}
              />
              <FlowNode
                entry={entry}
                position={positionByKey.get(entry.key) ?? index + 1}
                progress={entry.kind === 'course' ? childProgressByCourseId[entry.id] : null}
                canEdit={canReorderContentItem(entry)}
                courseCanEdit={courseCanEdit}
                draggedContentKey={draggedContentKey}
                dragOverContentKey={dragOverContentKey}
                dragOverContentMode={dragOverContentMode}
                dragApi={dragApi}
                onContextMenu={onContextMenu}
              />
            </React.Fragment>
          ))}

          <FlowConnector
            draggedContentKey={draggedContentKey}
            targetKey={entries.length > 0 ? entries[entries.length - 1].key : null}
            mode="after"
            onDropContent={onDropContent}
          />
          <button type="button" className="course-flow-add-node" onClick={onCreate}>
            <Plus size={20} />
            <span>Добавить узел</span>
          </button>
        </div>
      </div>
    </div>
  );
}
