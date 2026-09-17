import React from 'react';
import { Database, GripVertical, LockKeyhole } from 'lucide-react';
import { Link, useNavigate } from 'react-router-dom';
import { Badge, Card } from '../../../components/ui';
import IfEditor from '../../../components/IfEditor';
import { isAssignmentSolved, previewAssignmentDescription, previewAssignmentTitle } from '../courseAssignmentsModel';

function StaticLink({ to, children, agentId, agentRole, agentAction, agentState, agentKind, onContextMenu, onPrefetch }) {
  return (
    <Link
      to={to}
      className="block group"
      data-taskforge-automation-id={agentId}
      data-taskforge-agent-role={agentRole}
      data-taskforge-agent-action={agentAction}
      data-taskforge-agent-state={agentState}
      data-taskforge-agent-kind={agentKind}
      onContextMenuCapture={onContextMenu}
      onPointerEnter={onPrefetch}
      onPointerDown={onPrefetch}
      onFocus={onPrefetch}
    >
      {children}
    </Link>
  );
}

function CourseProgress({ progress }) {
  const current = progress || { loading: true, total: 0, solved: 0, percent: 0 };
  return (
    <div className="mt-5 space-y-2">
      <div className="text-xs font-medium text-neutral-500">
        {current.loading ? '—/—' : `${current.solved ?? 0}/${current.total ?? 0}`}
      </div>
      <div className="h-2 overflow-hidden rounded-full bg-neutral-200/70 dark:bg-white/10">
        <div
          className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500"
          style={{ width: `${current.total > 0 ? current.percent : 0}%` }}
        />
      </div>
    </div>
  );
}

function CourseContentCard({
  entry,
  position,
  canEdit,
  courseCanEdit,
  sortMode,
  isDragged,
  dropMode,
  dropEdge,
  progress,
  dragApi,
  onPrefetchCourse,
  onContextMenu,
}) {
  const navigate = useNavigate();
  const draggable = entry.kind !== 'locked' && canEdit && sortMode === 'default';

  if (entry.kind === 'locked') {
    const title = entry?.locked?.title || 'Продолжение закрыто';
    const requirement = entry?.locked?.requirement || 'Решите предыдущее задание, чтобы открыть продолжение.';
    return (
      <Card
        className="assignment-card assignment-card--locked h-full border-dashed"
        data-taskforge-agent-role="course-card-locked"
        data-taskforge-agent-kind="locked"
        data-taskforge-agent-state="locked"
        aria-label={`${title}. ${requirement}`}
      >
        <div className="assignment-card-main min-w-0">
          <div className="mb-3 inline-flex items-center gap-2 rounded-full border border-[rgba(var(--border)/0.7)] bg-[rgba(var(--muted)/0.28)] px-2.5 py-1 text-xs font-semibold text-neutral-500 dark:text-neutral-400">
            <LockKeyhole size={14} /> Закрыто
          </div>
          <div className="assignment-card-title-wrap">
            <div className="assignment-card-title">{title}</div>
          </div>
          <p className="mt-3 text-sm leading-6 text-neutral-500 dark:text-neutral-400">{requirement}</p>
        </div>
      </Card>
    );
  }

  const dragTargetProps = draggable ? {
    onDragEnter: (event) => dragApi.onEnter(event, entry),
    onDragOver: (event) => dragApi.onOver(event, entry),
    onDragLeave: (event) => dragApi.onLeave(event, entry),
    onDrop: (event) => dragApi.onDrop(event, entry),
  } : {};
  const dragHandle = draggable ? (
    <span
      className="absolute right-3 top-3 z-10 inline-flex h-8 w-8 items-center justify-center rounded-lg border border-[rgba(var(--border)/0.7)] bg-[rgba(var(--card)/0.92)] text-neutral-400 shadow-sm cursor-grab active:cursor-grabbing"
      draggable
      title="Перетащить"
      aria-label="Перетащить карточку"
      onClick={(event) => { event.preventDefault(); event.stopPropagation(); }}
      onMouseDown={(event) => event.stopPropagation()}
      onDragStart={(event) => dragApi.onStart(event, entry)}
      onDragEnd={dragApi.onEnd}
    >
      <GripVertical size={16} />
    </span>
  ) : null;

  if (entry.kind === 'course') {
    const child = entry.course;
    const title = child.title || `Курс ${position}`;
    const viewHref = `/course/${child.id}`;
    const editorHref = courseCanEdit && child.canEdit !== false ? `/courses/${child.id}/edit` : viewHref;
    const main = (
      <div className="assignment-card-main min-w-0">
        <div className="assignment-card-title-wrap">
          <div className="assignment-card-title" title={title}>{title}</div>
        </div>
        <p className={`mt-3 text-sm leading-6 line-clamp-3 ${child.description ? 'text-neutral-500' : 'text-neutral-400'}`}>
          {child.description || 'Описание пока не добавлено.'}
        </p>
        <CourseProgress progress={progress} />
      </div>
    );
    const className = [
      'assignment-card relative h-full transition hover:shadow-lg hover:-translate-y-0.5 border-[rgba(var(--accent)/0.35)] cursor-pointer',
      isDragged ? 'assignment-card--dragging' : '',
      dropMode && dropMode !== 'inside' ? 'dnd-insert-target' : '',
      dropMode === 'inside' ? 'dnd-nest-target' : '',
      dropEdge && dropMode !== 'inside' ? `dnd-insert-${dropEdge}` : '',
    ].filter(Boolean).join(' ');
    const staticCard = <Card className={className}>{main}</Card>;
    const editorCard = (
      <Card
        role="link"
        tabIndex={0}
        data-dnd-content-key={entry.key}
        {...dragTargetProps}
        onContextMenuCapture={(event) => onContextMenu?.(event, entry, canEdit)}
        onClick={() => dragApi.openAfterDrag(editorHref)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            navigate(editorHref);
          }
        }}
        className={className}
        title="Открыть курс"
      >
        {dragHandle}
        {main}
        {dropMode === 'inside' ? <div className="dnd-nest-hint">Вложить курс сюда</div> : null}
      </Card>
    );
    return (
      <IfEditor otherwise={<StaticLink to={viewHref} agentId={`course-${child.id}`} agentRole="course-card" agentAction="open-course" agentState={progress?.total > 0 && progress?.solved >= progress?.total ? "completed" : "incomplete"} onPrefetch={() => onPrefetchCourse?.(child.id)} onContextMenu={(event) => onContextMenu?.(event, entry, canEdit)}>{staticCard}</StaticLink>}>
        {canEdit ? editorCard : <StaticLink to={viewHref} agentId={`course-${child.id}`} agentRole="course-card" agentAction="open-course" agentState={progress?.total > 0 && progress?.solved >= progress?.total ? "completed" : "incomplete"} onPrefetch={() => onPrefetchCourse?.(child.id)} onContextMenu={(event) => onContextMenu?.(event, entry, canEdit)}>{staticCard}</StaticLink>}
      </IfEditor>
    );
  }

  const assignment = entry.assignment;
  const solved = isAssignmentSolved(assignment);
  const title = previewAssignmentTitle(assignment.title, `Задание ${position}`);
  const viewHref = `/assignment/${assignment.id}`;
  const editorHref = `/assignment/${assignment.id}/edit`;
  const hasMeta = Boolean(assignment.isAiDraft || assignment.isHidden || (assignment.lifecycleStatus && assignment.lifecycleStatus !== 'published'));
  const main = (
    <div className="assignment-card-main min-w-0">
      {assignment.type === "sql-test" && <Badge variant="outline"><Database size={13} /> SQL</Badge>}
      {hasMeta ? (
        <div className="assignment-card-heading">
          <div className="flex flex-wrap items-center gap-1.5">
            {assignment.isAiDraft ? <Badge variant="secondary">AI-черновик</Badge> : null}
            {assignment.isHidden ? <Badge variant="outline">скрыто</Badge> : null}
            {assignment.lifecycleStatus && assignment.lifecycleStatus !== 'published' && !assignment.isHidden ? <Badge variant="outline">{assignment.lifecycleStatus}</Badge> : null}
          </div>
        </div>
      ) : null}
      <div className="assignment-card-title-wrap">
        <div className={`assignment-card-title${solved ? ' opacity-70' : ''}`} title={title}>{title}</div>
      </div>
      {assignment.description ? <p className="mt-3 text-sm leading-6 text-neutral-500 line-clamp-3">{previewAssignmentDescription(assignment.description)}</p> : null}
      {assignment.tags ? <div className="mt-2 text-xs text-neutral-400 break-words">{assignment.tags}</div> : null}
    </div>
  );
  const className = [
    'assignment-card relative h-full transition hover:shadow-lg hover:-translate-y-0.5 cursor-pointer',
    solved ? 'assignment-card--solved' : '',
    isDragged ? 'assignment-card--dragging' : '',
    dropMode && dropMode !== 'inside' ? 'dnd-insert-target' : '',
    dropMode === 'inside' ? 'dnd-nest-target' : '',
    dropEdge && dropMode !== 'inside' ? `dnd-insert-${dropEdge}` : '',
  ].filter(Boolean).join(' ');
  const staticCard = <Card className={className}>{main}</Card>;
  const editorCard = (
    <Card
      role="link"
      tabIndex={0}
      data-dnd-content-key={entry.key}
      {...dragTargetProps}
      onContextMenuCapture={(event) => onContextMenu?.(event, entry, canEdit)}
      onClick={() => dragApi.openAfterDrag(editorHref)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          navigate(editorHref);
        }
      }}
      className={className}
      title="Открыть редактор задания"
    >
      {dragHandle}
      {main}
    </Card>
  );
  return (
    <IfEditor otherwise={<StaticLink to={viewHref} agentId={`assignment-${assignment.id}`} agentRole="assignment-card" agentAction="open-assignment" agentState={solved ? "solved" : "unsolved"} agentKind={assignment.type || assignment.kind || assignment.assignmentType || "code-test"} onContextMenu={(event) => onContextMenu?.(event, entry, canEdit)}>{staticCard}</StaticLink>}>
      {canEdit ? editorCard : <StaticLink to={viewHref} agentId={`assignment-${assignment.id}`} agentRole="assignment-card" agentAction="open-assignment" agentState={solved ? "solved" : "unsolved"} agentKind={assignment.type || assignment.kind || assignment.assignmentType || "code-test"} onContextMenu={(event) => onContextMenu?.(event, entry, canEdit)}>{staticCard}</StaticLink>}
    </IfEditor>
  );
}

export default React.memo(CourseContentCard);
