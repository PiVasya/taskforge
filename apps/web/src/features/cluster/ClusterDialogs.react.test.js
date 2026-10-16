import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import PrimarySwitchDialog from './PrimarySwitchDialog';
import ClusterLogCleanupDialog from './ClusterLogCleanupDialog';

jest.mock('../../api/systemStatus', () => ({
  cleanupClusterLogs: jest.fn(),
}));

jest.mock('../../utils/handleApiError', () => ({
  handleApiError: jest.fn(),
}));

jest.mock('./clusterModel', () => ({
  ...jest.requireActual('./clusterModel'),
  primarySwitchEligibility: () => ({ ready: true, reasons: [] }),
  logCleanupEligibility: node => ({ ready: node.ready !== false, revision: 100 }),
}));

describe('cluster dialogs preserve a deliberate server choice', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(async () => {
    await act(async () => { root.unmount(); });
    container.remove();
  });

  async function render(element) {
    await act(async () => { root.render(element); });
  }

  function selectedButton(selector) {
    return container.querySelector(`${selector}[aria-pressed="true"]`);
  }

  test('primary selection stays manual during telemetry refresh but resets when the active primary changes', async () => {
    const nodes = [{ id: 'A' }, { id: 'B' }, { id: 'C' }];
    const props = { open: true, activeId: 'A', initialTarget: 'B', busy: false, onClose: jest.fn(), onSubmit: jest.fn() };

    await render(<PrimarySwitchDialog {...props} nodes={nodes} />);
    expect(selectedButton('.tf-cluster-candidate')?.textContent).toContain('B');

    const c = [...container.querySelectorAll('.tf-cluster-candidate')].find(node => node.textContent.includes('C'));
    await act(async () => { c.click(); });
    expect(selectedButton('.tf-cluster-candidate')?.textContent).toContain('C');

    await render(<PrimarySwitchDialog {...props} nodes={nodes.map(node => ({ ...node }))} />);
    expect(selectedButton('.tf-cluster-candidate')?.textContent).toContain('C');

    await render(<PrimarySwitchDialog {...props} activeId="C" nodes={nodes.map(node => ({ ...node }))} />);
    expect(selectedButton('.tf-cluster-candidate')?.textContent).toContain('B');
  });

  test('cleanup selection survives polling and reinitializes when available nodes change or dialog reopens', async () => {
    const nodes = [{ id: 'A', ready: true }, { id: 'B', ready: true }];
    const props = { open: true, onClose: jest.fn(), onFinished: jest.fn(), notify: {} };

    await render(<ClusterLogCleanupDialog {...props} nodes={nodes} />);
    expect(container.querySelectorAll('.tf-cluster-log-cleanup-node[aria-pressed="true"]')).toHaveLength(2);

    const b = [...container.querySelectorAll('.tf-cluster-log-cleanup-node')].find(node => node.textContent.includes('B'));
    await act(async () => { b.click(); });
    expect(container.querySelectorAll('.tf-cluster-log-cleanup-node[aria-pressed="true"]')).toHaveLength(1);

    await render(<ClusterLogCleanupDialog {...props} nodes={nodes.map(node => ({ ...node }))} />);
    expect(container.querySelectorAll('.tf-cluster-log-cleanup-node[aria-pressed="true"]')).toHaveLength(1);

    await render(<ClusterLogCleanupDialog {...props} nodes={[{ id: 'A', ready: false }, { id: 'B', ready: true }]} />);
    expect(selectedButton('.tf-cluster-log-cleanup-node')?.textContent).toContain('B');

    await render(<ClusterLogCleanupDialog {...props} open={false} nodes={nodes} />);
    await render(<ClusterLogCleanupDialog {...props} nodes={nodes} />);
    expect(container.querySelectorAll('.tf-cluster-log-cleanup-node[aria-pressed="true"]')).toHaveLength(2);
  });
});
