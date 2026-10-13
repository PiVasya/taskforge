import React from 'react';

export default function TaskGraphPreviewMap({ graph }) {
  const nodes = graph?.nodes || [];
  if (!nodes.length) return null;
  const byId = new Map(nodes.map((x) => [x.id, x]));
  const left = Math.min(...nodes.map((x) => x.position.x)) - 40;
  const top = Math.min(...nodes.map((x) => x.position.y)) - 40;
  const right = Math.max(...nodes.map((x) => x.position.x + x.width)) + 40;
  const bottom = Math.max(...nodes.map((x) => x.position.y + x.height)) + 40;
  const width = right - left;
  const height = bottom - top;
  return <details className="mt-3 rounded-xl border p-3" data-testid="import-map-preview">
    <summary className="cursor-pointer text-sm font-semibold">{'Будущая карта'}: {nodes.length} {'узлов'}, {graph.edges.length} {'связей'}</summary>
    <div className="mt-2 max-h-96 overflow-auto rounded-lg bg-[rgb(var(--card))]">
      <svg role="img" aria-label={'Карта после импорта'} viewBox={`${left} ${top} ${width} ${height}`}
        style={{ width: '100%', height: 360, maxHeight: '60vh' }}>
        <defs><marker id="task-graph-preview-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor" /></marker></defs>
        {graph.edges.map((edge) => {
          const a = byId.get(edge.source); const b = byId.get(edge.target);
          if (!a || !b) return null;
          return <line key={edge.id} x1={a.position.x + a.width} y1={a.position.y + a.height / 2}
            x2={b.position.x} y2={b.position.y + b.height / 2} stroke="currentColor" strokeWidth="3" opacity="0.6" markerEnd="url(#task-graph-preview-arrow)" />;
        })}
        {nodes.map((node) => <g key={node.id}>
          <title>{node.label}</title>
          <rect x={node.position.x} y={node.position.y} width={node.width} height={node.height} rx="18" stroke="currentColor" strokeWidth="3" fill="rgb(var(--card))" />
          <text x={node.position.x + 16} y={node.position.y + 42} fill="currentColor" fontSize="24">{String(node.label).slice(0, 25)}</text>
          <text x={node.position.x + 16} y={node.position.y + 76} fill="currentColor" fontSize="18">{node.type}</text>
        </g>)}
      </svg>
    </div>
  </details>;
}
