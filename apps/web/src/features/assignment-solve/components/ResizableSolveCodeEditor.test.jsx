import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import ResizableSolveCodeEditor from './ResizableSolveCodeEditor';

jest.mock('../../../components/CodeEditor', () => {
  const React = require('react');
  return function MockCodeEditor({ height }) {
    return React.createElement('div', { 'data-testid': 'mock-code-editor', 'data-height': height });
  };
});

function pointer(type, y, pointerId = 1) {
  const event = new MouseEvent(type, { bubbles: true, button: 0, clientY: y });
  Object.defineProperty(event, 'pointerId', { value: pointerId });
  return event;
}

describe('solution code editor vertical resize', () => {
  let root;
  let container;

  beforeEach(() => {
    window.localStorage.clear();
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  test('dragging changes only the height and persists independently per task', () => {
    act(() => root.render(<ResizableSolveCodeEditor key="one" assignmentId="one" defaultHeight={380} />));
    const surface = container.querySelector('.solve-editor-resizable__surface');
    const grip = container.querySelector('[role="separator"]');
    expect(grip.parentElement).toBe(surface); // Only a small resize corner inside the editor, no extra drag strip.
    expect(grip.querySelector('svg')).not.toBeNull();
    expect(surface.nextElementSibling).toBeNull();
    expect(surface.style.height).toBe('380px');
    expect(surface.style.width).toBe('');
    expect(container.querySelector('[data-testid="mock-code-editor"]').getAttribute('data-height')).toBe('100%');

    act(() => {
      grip.dispatchEvent(pointer('pointerdown', 100));
      grip.dispatchEvent(pointer('pointermove', 290));
      grip.dispatchEvent(pointer('pointerup', 290));
    });
    expect(surface.style.height).toBe('570px');
    expect(window.localStorage.getItem('taskforge:solve-editor-height:v1:one')).toBe('570');

    act(() => root.render(<ResizableSolveCodeEditor key="two" assignmentId="two" defaultHeight={460} />));
    expect(container.querySelector('.solve-editor-resizable__surface').style.height).toBe('460px');

    act(() => root.render(<ResizableSolveCodeEditor key="one" assignmentId="one" defaultHeight={380} />));
    expect(container.querySelector('.solve-editor-resizable__surface').style.height).toBe('570px');
  });

  test('keyboard resize works and never adjusts width', () => {
    act(() => root.render(<ResizableSolveCodeEditor assignmentId="one" defaultHeight={380} />));
    const surface = container.querySelector('.solve-editor-resizable__surface');
    const grip = container.querySelector('[role="separator"]');
    act(() => grip.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, shiftKey: true })));
    expect(surface.style.height).toBe('430px');
    act(() => grip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true })));
    expect(surface.style.height).toBe('200px');
    expect(surface.style.width).toBe('');
  });
});
