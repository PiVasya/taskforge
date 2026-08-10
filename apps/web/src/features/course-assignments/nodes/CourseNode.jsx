import React from 'react';
import { FolderTree } from 'lucide-react';
import { CourseMapHandles, NodeAccessBadges, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function CourseNode({ data, selected }) {
  const course = data?.entity || {};
  const progress = data?.progress || { total: 0, solved: 0, percent: 0 };
  const hiddenFromStudents = Boolean(data?.editorMode && (course?.isHiddenForStudents || course?.isHiddenFromStudents));
  const inheritedHidden = hiddenFromStudents && !course?.isHiddenFromStudents;
  return (
    <div
      className={`course-map-node course-map-node--course${selected ? ' is-selected' : ''}${hiddenFromStudents ? ' is-hidden-from-students' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="course"
      data-taskforge-entity-id={course.id || data?.entityId}
      data-taskforge-agent-action="focus-course"
      data-taskforge-admin-hidden={hiddenFromStudents ? 'true' : undefined}
      aria-label={`Курс ${course.title || 'Без названия'}. ${hiddenFromStudents ? 'Скрыт от учеников. ' : ''}Решено ${progress.solved || 0} из ${progress.total || 0}.`}
    >
      <CourseMapHandles />
      <NodeAccessBadges effects={data?.accessEffects} editorMode={data?.editorMode} />
      <NodeTopline icon={FolderTree} kicker="Курс · развилка" badge={hiddenFromStudents ? 'СКРЫТ' : null} />
      {hiddenFromStudents ? <div className="course-map-hidden-course-warning">{inheritedHidden ? 'Скрыт вместе с родительским курсом' : 'Не существует для учеников'}</div> : null}
      <div className="course-map-node-title">{course.title || 'Без названия'}</div>
      <div className="course-map-course-progress-row"><span>{progress.solved || 0} / {progress.total || 0}</span><span>{progress.percent || 0}%</span></div>
      <div className="course-map-course-progress"><span style={{ width: `${Math.max(0, Math.min(100, Number(progress.percent) || 0))}%` }} /></div>
      <NodeHover entity={course} actionLabel="Показать ветку" onAction={data?.onFocus} placement="right">
        {hiddenFromStudents ? <div className="course-map-hover-hidden-note">{inheritedHidden ? 'Этот курс скрыт, потому что скрыт один из его родителей.' : 'Курс и всё его поддерево скрыты от обычных пользователей.'}</div> : null}
        <div className="course-map-hover-course-stat">
          <span>{progress.total || 0} заданий</span>
          <span>{progress.solved || 0} решено</span>
          <span>{progress.percent || 0}%</span>
        </div>
      </NodeHover>
    </div>
  );
}
