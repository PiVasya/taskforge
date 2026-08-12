import React from 'react';
import { Handle, Position } from 'reactflow';
import { LockKeyhole } from 'lucide-react';
import { NodeTopline } from './CourseMapNodePrimitives';

export default function LockedNode({ data }) {
  const settings = data?.settings || {};
  const title = settings.title || 'Продолжение закрыто';
  const requirement = settings.requirement || 'Решите предыдущее задание, чтобы открыть продолжение.';

  return (
    <div
      className="course-map-node course-map-node--locked"
      data-taskforge-agent-role="course-map-locked-node"
      data-taskforge-agent-kind="locked"
      data-taskforge-agent-state="locked"
      aria-label={`${title}. ${requirement}`}
    >
      <Handle
        id="in"
        type="target"
        position={Position.Left}
        isConnectableStart={false}
        isConnectableEnd={false}
        className="course-map-handle course-map-handle--in"
      />
      <NodeTopline icon={LockKeyhole} title={title} />
      <div className="course-map-locked-requirement">{requirement}</div>
    </div>
  );
}
