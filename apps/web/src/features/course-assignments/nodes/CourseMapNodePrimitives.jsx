import React from 'react';
import { Handle, Position } from 'reactflow';
import { ArrowRight, CheckCircle2, Circle, ListOrdered, LockKeyhole } from 'lucide-react';
import { previewAssignmentDescription } from '../courseAssignmentsModel';

export function CourseMapHandles() {
  return (
    <>
      <Handle
        id="in"
        type="target"
        position={Position.Left}
        isConnectableStart={false}
        isConnectableEnd
        className="course-map-handle course-map-handle--in"
      />
      <Handle
        id="out"
        type="source"
        position={Position.Right}
        isConnectableStart
        isConnectableEnd={false}
        className="course-map-handle course-map-handle--out"
      />
    </>
  );
}


export function NodeAccessBadges({ effects, editorMode }) {
  if (!editorMode || !effects || (!effects.hidden && !effects.sequential)) return null;
  const hiddenHint = effects.hiddenMixed
    ? 'Скрытие действует только для части входящих путей'
    : 'Участок полностью скрыт до выполнения условий';
  const sequentialHint = effects.sequentialMixed
    ? 'Пошаговое открытие действует только для части входящих путей'
    : 'Задания на участке открываются по одному';

  return (
    <div className="course-map-node-access-badges nodrag nopan" aria-label="Ограничения участка">
      {effects.hidden ? (
        <span
          className={`course-map-node-access-badge is-hidden${effects.hiddenMixed ? ' is-mixed' : ''}`}
          data-hint={hiddenHint}
          aria-label={hiddenHint}
          role="img"
        >
          <LockKeyhole size={13} strokeWidth={2.2} />
        </span>
      ) : null}
      {effects.sequential ? (
        <span
          className={`course-map-node-access-badge is-sequential${effects.sequentialMixed ? ' is-mixed' : ''}`}
          data-hint={sequentialHint}
          aria-label={sequentialHint}
          role="img"
        >
          <ListOrdered size={13} strokeWidth={2.2} />
        </span>
      ) : null}
    </div>
  );
}

export function NodeTopline({ icon: Icon, kicker, badge }) {
  return (
    <div className="course-map-node-topline">
      <span className="course-map-node-icon">{Icon ? <Icon size={17} /> : null}</span>
      <span className="course-map-node-kicker">{kicker}{badge ? <b>{badge}</b> : null}</span>
    </div>
  );
}

export function AssignmentFooter({ assignment, fallback }) {
  const solved = Boolean(assignment?.solvedByCurrentUser || assignment?.isSolved || assignment?.completedByCurrentUser || assignment?.progressStatus === 'solved');
  const language = assignment?.language || (Array.isArray(assignment?.allowedLanguages) ? assignment.allowedLanguages[0] : '') || fallback || '';
  return (
    <div className="course-map-node-footer">
      <span>{language || fallback || 'Задание'}</span>
      <span className={`course-map-node-status${solved ? ' is-solved' : ''}`}>
        {solved ? <CheckCircle2 size={12} /> : <Circle size={11} />}
        {solved ? 'решено' : 'не решено'}
      </span>
    </div>
  );
}

export function NodeHover({ entity, meta, actionLabel = 'Перейти к заданию', onAction, placement = 'bottom', children }) {
  const description = previewAssignmentDescription(entity?.description || '') || 'Описание пока не добавлено.';
  return (
    <div className={`course-map-node-hover course-map-node-hover--${placement}`}>
      <div className="course-map-node-hover-body">
        <div className="course-map-hover-description">{description}</div>
        {children}
        {meta ? <div className="course-map-hover-meta">{meta}</div> : null}
      </div>
      {onAction ? (
        <button type="button" className="course-map-node-hover-action nodrag nopan" onClick={(event) => { event.stopPropagation(); onAction(); }}>
          {actionLabel}<ArrowRight size={14} />
        </button>
      ) : null}
    </div>
  );
}
