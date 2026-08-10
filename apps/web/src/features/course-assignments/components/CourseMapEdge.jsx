import React from 'react';
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath } from 'reactflow';

export default function CourseMapEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  style,
  data,
  interactionWidth = 20,
}) {
  const [edgePath, labelX, labelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
    borderRadius: 12,
  });
  const badges = Array.isArray(data?.effectBadges) ? data.effectBadges : [];

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        markerEnd={markerEnd}
        style={style}
        interactionWidth={interactionWidth}
      />
      {badges.length ? (
        <EdgeLabelRenderer>
          <div
            className="course-map-edge-badges nodrag nopan"
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
            aria-hidden="true"
          >
            {badges.map((badge) => (
              <span
                key={`${badge.kind}:${badge.transition}`}
                className={`course-map-edge-badge is-${badge.kind} is-${badge.transition}`}
              >
                <b>{badge.kind === 'hidden' ? 'Скрытие' : 'По одному'}</b>
                <i>{badge.transition === 'start' ? 'старт' : 'конец'}</i>
              </span>
            ))}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}
