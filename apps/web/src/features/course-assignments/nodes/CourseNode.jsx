import React from 'react';
import { FolderTree } from 'lucide-react';
import { activateCourseMapNodeForAgent, CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

function taskWord(value) {
  const number = Math.abs(Number(value) || 0) % 100;
  const tail = number % 10;
  if (number > 10 && number < 20) return 'заданий';
  if (tail === 1) return 'задание';
  if (tail > 1 && tail < 5) return 'задания';
  return 'заданий';
}

export default function CourseNode({ data, selected }) {
  const course = data?.entity || {};
  const progress = data?.progress || { total: 0, solved: 0, percent: 0 };
  const hiddenFromStudents = Boolean(data?.editorMode && (course?.isHiddenForStudents || course?.isHiddenFromStudents));
  const groupRestricted = Boolean(data?.editorMode && !hiddenFromStudents && (course?.isGroupRestrictedForStudents || course?.isPublic === false));
  const inheritedHidden = hiddenFromStudents && !course?.isHiddenFromStudents;
  const inheritedGroupRestriction = groupRestricted && course?.isPublic !== false;
  const total = Math.max(0, Number(progress.total) || 0);
  const solved = Math.max(0, Math.min(total, Number(progress.solved) || 0));
  const percent = Math.max(0, Math.min(100, Number(progress.percent) || 0));
  return (
    <div
      className={`course-map-node course-map-node--course${selected ? ' is-selected' : ''}${hiddenFromStudents ? ' is-hidden-from-students' : ''}`}
      data-taskforge-automation-id={`course-${course.id || data?.entityId}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="course"
      data-taskforge-entity-id={course.id || data?.entityId}
      data-taskforge-agent-kind="course"
      data-taskforge-agent-action="focus-course"
      onClick={(event) => activateCourseMapNodeForAgent(event, data, data?.onFocus || data?.onOpen)}
      data-taskforge-admin-hidden={hiddenFromStudents ? 'true' : undefined}
      aria-label={`Курс ${course.title || 'Без названия'}. ${hiddenFromStudents ? 'Скрыт от учеников. ' : groupRestricted ? 'Доступ по группам. ' : ''}Решено ${solved} из ${total}.`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={FolderTree} title={course.title} badge={hiddenFromStudents ? 'СКРЫТ' : groupRestricted ? 'ГРУППЫ' : null} />
      {hiddenFromStudents ? <div className="course-map-hidden-course-warning">{inheritedHidden ? 'Скрыт вместе с родительским курсом' : 'Не существует для учеников'}</div> : null}
      {groupRestricted && inheritedGroupRestriction ? <div className="course-map-hidden-course-warning">Ограничен группами родительского курса</div> : null}
      <div className="course-map-course-progress-row"><span>Решено {solved} из {total}</span><span>{percent}%</span></div>
      <div className="course-map-course-progress" aria-label={`Решено ${solved} из ${total}`}><span style={{ width: `${percent}%` }} /></div>
      <NodeHover entity={course} actionLabel="Показать ветку" onAction={data?.onFocus} placement="right">
        {hiddenFromStudents ? <div className="course-map-hover-hidden-note">{inheritedHidden ? 'Этот курс скрыт, потому что скрыт один из его родителей.' : 'Курс и всё его поддерево скрыты от обычных пользователей.'}</div> : null}
        {groupRestricted && inheritedGroupRestriction ? <div className="course-map-hover-hidden-note">Доступ ограничен группами в родительском курсе.</div> : null}
        <div className="course-map-hover-course-stat">
          <span>Всего: {total} {taskWord(total)}</span>
          <span>Решено: {solved}</span>
          <span>{percent}%</span>
        </div>
      </NodeHover>
    </div>
  );
}
