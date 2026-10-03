import React from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import CodeEditor, { isPlainTextEditorStyle } from './CodeEditor';
import { useEditorUiSettings } from '../contexts/UiSettingsContext';

jest.mock('../contexts/UiSettingsContext', () => ({
  useEditorUiSettings: jest.fn(),
  useUiTheme: () => ({ mode: 'light' }),
}));

jest.mock('../devtools/frontendDiagnostics', () => ({
  logFrontendEvent: jest.fn(),
}));

describe('CodeEditor plain text mode', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    useEditorUiSettings.mockReturnValue({ codeEditorStyle: 'mono' });
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    delete window.__TASKFORGE_BROWSER_AUTOMATION__;
    jest.clearAllMocks();
  });

  test('treats the legacy mono setting as a real plain text editor mode', () => {
    expect(isPlainTextEditorStyle('mono')).toBe(true);
    expect(isPlainTextEditorStyle('color')).toBe(false);
  });

  test('renders a native textarea and forwards text changes', () => {
    const onChange = jest.fn();

    act(() => {
      root.render(
        <CodeEditor
          value="print('old')"
          onChange={onChange}
          automationRole="code-editor"
        />,
      );
    });

    const shell = container.querySelector('[data-taskforge-editor-kind="plain-text"]');
    const textarea = shell?.querySelector('textarea');

    expect(shell).not.toBeNull();
    expect(textarea).not.toBeNull();
    expect(textarea.value).toBe("print('old')");
    expect(container.querySelector('[data-taskforge-editor-kind="monaco"]')).toBeNull();
    expect(textarea.getAttribute('spellcheck')).toBe('false');
    expect(textarea.getAttribute('autocorrect')).toBe('off');
    expect(textarea.getAttribute('autocapitalize')).toBe('none');

    act(() => {
      textarea.value = "print('new')";
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });

    expect(onChange).toHaveBeenCalledWith("print('new')");
  });
});
