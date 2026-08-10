import React from 'react';
import { FolderTree } from 'lucide-react';
import { CourseMapHandles, NodeHover, NodeTopline } from './CourseMapNodePrimitives';

export default function CourseNode({ data, selected }) {
  const course = data?.entity || {};
  const progress = data?.progress || { total: 0, solved: 0, percent: 0 };
  return (
    <div
      className={`course-map-node course-map-node--course${selected ? ' is-selected' : ''}`}
      data-taskforge-agent-role="course-map-node"
      data-taskforge-entity="course"
      data-taskforge-entity-id={course.id || data?.entityId}
      data-taskforge-agent-action="focus-course"
      aria-label={`Курс ${course.title || 'Без названия'}. Решено ${progress.solved || 0} из ${progress.total || 0}.`}
    >
      <CourseMapHandles />
      <NodeTopline icon={FolderTree} kicker="Курс · развилка" />
      <div className="course-map-node-title">{course.title || 'Без названия'}</div>
      <div className="course-map-course-progress-row"><span>{progress.solved || 0} / {progress.total || 0}</span><span>{progress.percent || 0}%</span></div>
      <div className="course-map-course-progress"><span style={{ width: `${Math.max(0, Math.min(100, Number(progress.percent) || 0))}%` }} /></div>
      <NodeHover entity={course} actionLabel="Показать ветку" onAction={data?.onFocus} placement="right">
        <div className="course-map-hover-course-stat">
          <span>{progress.total || 0} заданий</span>
          <span>{progress.solved || 0} решено</span>
          <span>{progress.percent || 0}%</span>
        </div>
      </NodeHover>
    </div>
  );
}
