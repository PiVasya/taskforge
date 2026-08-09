import React from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Badge, Card } from '../../../components/ui';
import IfEditor from '../../../components/IfEditor';
import { isAssignmentSolved, previewAssignmentDescription, previewAssignmentTitle } from '../courseAssignmentsModel';

function StaticLink({ to, children, agentId, agentRole, agentAction, agentState, agentKind }) {
  return (
    <Link
      to={to}
      className="block group"
      data-taskforge-automation-id={agentId}
      data-taskforge-agent-role={agentRole}
      data-taskforge-agent-action={agentAction}
      data-taskforge-agent-state={agentState}
      data-taskforge-agent-kind={agentKind}
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
  progress,
  dragApi,
}) {
  const navigate = useNavigate();
  const isDropTarget = Boolean(dropMode);
  const draggable = canEdit && sortMode === 'default';

  const sharedDragProps = draggable ? {
    draggable: true,
    onDragStart: (event) => dragApi.onStart(event, entry),
    onDragEnter: (event) => dragApi.onEnter(event, entry),
    onDragOver: (event) => dragApi.onOver(event, entry),
    onDragLeave: () => dragApi.onLeave(entry),
    onDrop: (event) => dragApi.onDrop(event, entry),
    onDragEnd: dragApi.onEnd,
  } : {};

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
      'assignment-card h-full transition hover:shadow-lg hover:-translate-y-0.5 border-[rgba(var(--accent)/0.35)]',
      isDragged ? 'assignment-card--dragging' : '',
      isDropTarget ? 'assignment-card--drop-target' : '',
      dropMode === 'inside' ? 'ring-2 ring-[rgb(var(--accent))]' : '',
      dropMode === 'after' ? 'border-b-4 border-b-[rgb(var(--accent))]' : '',
      dropMode === 'before' ? 'border-t-4 border-t-[rgb(var(--accent))]' : '',
      draggable ? 'cursor-move' : 'cursor-pointer',
    ].filter(Boolean).join(' ');
    const staticCard = <Card className={className}>{main}</Card>;
    const editorCard = (
      <Card
        role="link"
        tabIndex={0}
        {...sharedDragProps}
        onClick={() => dragApi.openAfterDrag(editorHref)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            navigate(editorHref);
          }
        }}
        className={className}
        title={sortMode === 'default' ? 'Перетащи карточку, чтобы изменить общий порядок' : 'Открыть курс'}
      >
        {main}
      </Card>
    );
    return (
      <IfEditor otherwise={<StaticLink to={viewHref} agentId={`course-${child.id}`} agentRole="course-card" agentAction="open-course" agentState={progress?.total > 0 && progress?.solved >= progress?.total ? "completed" : "incomplete"}>{staticCard}</StaticLink>}>
        {canEdit ? editorCard : <StaticLink to={viewHref} agentId={`course-${child.id}`} agentRole="course-card" agentAction="open-course" agentState={progress?.total > 0 && progress?.solved >= progress?.total ? "completed" : "incomplete"}>{staticCard}</StaticLink>}
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
      {hasMeta ? (
        <div className="assignment-card-heading">
          <div className="flex flex-wrap items-center gap-1.5">
            {assignment.isAiDraft ? <Badge variant="secondary">AI-черновик</Badge> : null}
            {assignment.isHidden ? <Badge variant="outline">скрыто</Badge> : null}
            {assignment.lifecycleStatus && assignment.lifecycleStatus !== 'published' ? <Badge variant="outline">{assignment.lifecycleStatus}</Badge> : null}
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
    'assignment-card h-full transition hover:shadow-lg hover:-translate-y-0.5',
    solved ? 'assignment-card--solved' : '',
    isDragged ? 'assignment-card--dragging' : '',
    isDropTarget ? 'assignment-card--drop-target' : '',
    dropMode === 'after' ? 'border-b-4 border-b-[rgb(var(--accent))]' : '',
    dropMode === 'before' ? 'border-t-4 border-t-[rgb(var(--accent))]' : '',
    draggable ? 'cursor-move' : 'cursor-pointer',
  ].filter(Boolean).join(' ');
  const staticCard = <Card className={className}>{main}</Card>;
  const editorCard = (
    <Card
      role="link"
      tabIndex={0}
      {...sharedDragProps}
      onClick={() => dragApi.openAfterDrag(editorHref)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          navigate(editorHref);
        }
      }}
      className={className}
      title={sortMode === 'default' ? 'Перетащи карточку, чтобы изменить общий порядок' : 'Открыть редактор задания'}
    >
      {main}
    </Card>
  );
  return (
    <IfEditor otherwise={<StaticLink to={viewHref} agentId={`assignment-${assignment.id}`} agentRole="assignment-card" agentAction="open-assignment" agentState={solved ? "solved" : "unsolved"} agentKind={assignment.type || assignment.kind || assignment.assignmentType || "code-test"}>{staticCard}</StaticLink>}>
      {canEdit ? editorCard : <StaticLink to={viewHref} agentId={`assignment-${assignment.id}`} agentRole="assignment-card" agentAction="open-assignment" agentState={solved ? "solved" : "unsolved"} agentKind={assignment.type || assignment.kind || assignment.assignmentType || "code-test"}>{staticCard}</StaticLink>}
    </IfEditor>
  );
}

export default React.memo(CourseContentCard);
