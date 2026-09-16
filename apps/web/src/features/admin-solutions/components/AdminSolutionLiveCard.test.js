import React from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import AdminSolutionLiveCard from './AdminSolutionLiveCard';
import { loadAdminSolutionLiveDetail } from '../../../api/adminSolutionLive';

jest.mock('../../../api/adminSolutionLive', () => ({
  loadAdminSolutionLiveDetail: jest.fn(),
}));

jest.mock('../../../components/CodeEditor', () => ({
  __esModule: true,
  default: ({ value }) => <pre data-testid="code-editor">{value}</pre>,
}));

jest.mock('../../../components/math/MathAttemptReview', () => ({
  __esModule: true,
  default: ({ dto }) => <div data-testid="math-review">{dto?.scorePercent}</div>,
}));

describe('AdminSolutionLiveCard', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    loadAdminSolutionLiveDetail.mockReset();
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  test('opens code from the detail already loaded by the live feed without another request', async () => {
    await act(async () => {
      root.render(
        <AdminSolutionLiveCard
          item={{
            key: 'code:solution-1',
            kind: 'code',
            itemId: 'solution-1',
            assignmentId: 'assignment-1',
            assignmentTitle: 'A + B',
            userLabel: 'Иван Иванов',
            occurredAtUtc: '2026-09-16T12:00:00Z',
            status: 'Accepted',
            detail: {
              submittedCode: 'print(42)',
              language: 'python',
              status: 'Accepted',
            },
          }}
        />
      );
    });

    const openButton = [...container.querySelectorAll('button')]
      .find((button) => button.textContent === 'Показать код');

    expect(openButton).toBeTruthy();

    await act(async () => {
      openButton.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(container.querySelector('[data-testid="code-editor"]')?.textContent).toBe('print(42)');
    expect(loadAdminSolutionLiveDetail).not.toHaveBeenCalled();
  });

  test('loads missing live details on demand and then shows the code', async () => {
    loadAdminSolutionLiveDetail.mockResolvedValue({
      submittedCode: 'Console.WriteLine(7);',
      language: 'csharp',
      status: 'Accepted',
    });

    const item = {
      key: 'code:solution-2',
      kind: 'code',
      itemId: 'solution-2',
      assignmentId: 'assignment-2',
      assignmentTitle: 'Hello',
      userLabel: 'Пётр Петров',
      occurredAtUtc: '2026-09-16T12:05:00Z',
      status: 'Accepted',
    };

    await act(async () => {
      root.render(<AdminSolutionLiveCard item={item} />);
    });

    const openButton = [...container.querySelectorAll('button')]
      .find((button) => button.textContent === 'Показать код');

    await act(async () => {
      openButton.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      await Promise.resolve();
    });

    expect(loadAdminSolutionLiveDetail).toHaveBeenCalledWith(item);
    expect(container.querySelector('[data-testid="code-editor"]')?.textContent).toBe('Console.WriteLine(7);');
  });
});
