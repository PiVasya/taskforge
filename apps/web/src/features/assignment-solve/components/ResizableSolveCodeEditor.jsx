import React, { useCallback, useEffect, useRef } from 'react';
import CodeEditor from '../../../components/CodeEditor';
import {
  MIN_SOLVE_EDITOR_HEIGHT,
  MAX_SOLVE_EDITOR_HEIGHT,
  normalizeSolveEditorHeight,
  readSolveEditorHeight,
  writeSolveEditorHeight,
} from '../solveEditorHeight';

function ResizableSolveCodeEditor({ assignmentId, defaultHeight = 380, ...editorProps }) {
  const surfaceRef = useRef(null);
  const gripRef = useRef(null);
  const initialHeight = useRef(readSolveEditorHeight(assignmentId, defaultHeight));
  const heightRef = useRef(initialHeight.current);
  const dragRef = useRef(null);

  const setHeight = useCallback((height) => {
    const next = normalizeSolveEditorHeight(height, defaultHeight);
    if (next === heightRef.current) return;
    heightRef.current = next;
    if (surfaceRef.current) surfaceRef.current.style.height = `${next}px`;
    if (gripRef.current) gripRef.current.setAttribute('aria-valuenow', String(next));
  }, [defaultHeight]);

  const finishDrag = useCallback((event) => {
    const drag = dragRef.current;
    if (!drag || event.pointerId !== drag.pointerId) return;
    dragRef.current = null;
    if (gripRef.current?.hasPointerCapture?.(event.pointerId)) {
      gripRef.current.releasePointerCapture(event.pointerId);
    }
    writeSolveEditorHeight(assignmentId, heightRef.current);
  }, [assignmentId]);

  const beginDrag = useCallback((event) => {
    if (event.button !== 0 || dragRef.current) return;
    event.preventDefault();
    dragRef.current = {
      pointerId: event.pointerId,
      startY: event.clientY,
      startHeight: heightRef.current,
    };
    event.currentTarget.setPointerCapture?.(event.pointerId);
  }, []);

  const moveDrag = useCallback((event) => {
    const drag = dragRef.current;
    if (!drag || event.pointerId !== drag.pointerId) return;
    setHeight(drag.startHeight + event.clientY - drag.startY);
  }, [setHeight]);

  const onGripKeyDown = useCallback((event) => {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown'
      && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    const amount = event.shiftKey ? 50 : 20;
    const next = event.key === 'Home' ? MIN_SOLVE_EDITOR_HEIGHT
      : event.key === 'End' ? MAX_SOLVE_EDITOR_HEIGHT
        : heightRef.current + (event.key === 'ArrowDown' ? amount : -amount);
    setHeight(next);
    writeSolveEditorHeight(assignmentId, normalizeSolveEditorHeight(next));
  }, [assignmentId, setHeight]);

  useEffect(() => () => {
    if (dragRef.current) writeSolveEditorHeight(assignmentId, heightRef.current);
  }, [assignmentId]);

  return (
    <div className="solve-editor-resizable" data-taskforge-editor-height-scope="assignment">
      <div
        ref={surfaceRef}
        className="solve-editor-resizable__surface"
        style={{ height: initialHeight.current }}
      >
        <CodeEditor {...editorProps} height="100%" />
        <div
          ref={gripRef}
          role="separator"
          aria-label="Изменить высоту редактора кода"
          aria-orientation="horizontal"
          aria-valuemin={MIN_SOLVE_EDITOR_HEIGHT}
          aria-valuemax={MAX_SOLVE_EDITOR_HEIGHT}
          aria-valuenow={initialHeight.current}
          tabIndex={0}
          className="solve-editor-resizable__handle"
          onPointerDown={beginDrag}
          onPointerMove={moveDrag}
          onPointerUp={finishDrag}
          onPointerCancel={finishDrag}
          onLostPointerCapture={finishDrag}
          onKeyDown={onGripKeyDown}
        >
          <svg viewBox="0 0 16 16" fill="none" className="solve-editor-resizable__grip" aria-hidden="true">
            <path d="M5 13L13 5M10 13L13 10" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
          </svg>
        </div>
      </div>
    </div>
  );
}

export default React.memo(ResizableSolveCodeEditor);
