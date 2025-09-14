import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, fireEvent, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import Modal from './Modal.svelte';

// Mock the dependent modal subcomponents to avoid deep rendering concerns
vi.mock('./ConfirmModal.svelte', () => ({
  default: ({ props }: any) => {
    const { modal } = props;
    return {
      $$render: () => `<div data-testid="confirm-modal">confirm-${modal?.id ?? 'unknown'}</div>`
    };
  }
}));
vi.mock('./AlertModal.svelte', () => ({
  default: ({ props }: any) => {
    const { modal } = props;
    return {
      $$render: () => `<div data-testid="alert-modal">alert-${modal?.id ?? 'unknown'}</div>`
    };
  }
}));
vi.mock('./ComponentModal.svelte', () => ({
  default: ({ props }: any) => {
    const { modal } = props;
    return {
      $$render: () => `<div data-testid="component-modal">component-${modal?.id ?? 'unknown'}</div>`
    };
  }
}));

// Create a simple in-memory mock store that mimics Svelte writable store API
type ModalEntry = {
  id: string | number;
  type: 'alert' | 'confirm' | 'component';
  onConfirm?: () => Promise<void> | void;
  onCancel?: () => Promise<void> | void;
  component?: any;
};

function createMockModalStore(initial: ModalEntry[] = []) {
  let value = initial.slice();
  const subscribers: Array<(v: ModalEntry[]) => void> = [];
  return {
    get current() {
      return value;
    },
    set(newVal: ModalEntry[]) {
      value = newVal.slice();
      subscribers.forEach((s) => s(value));
    },
    subscribe(run: (v: ModalEntry[]) => void) {
      run(value);
      subscribers.push(run);
      return () => {
        const i = subscribers.indexOf(run);
        if (i >= 0) subscribers.splice(i, 1);
      };
    },
    open(entry: ModalEntry) {
      value = [...value, entry];
      subscribers.forEach((s) => s(value));
    },
    close() {
      value = value.slice(0, -1);
      subscribers.forEach((s) => s(value));
    }
  };
}

// Mock the store module used by Modal.svelte
const storeMock = createMockModalStore();

vi.mock('./modalStore.js', () => {
  return {
    modalStore: {
      subscribe: storeMock.subscribe.bind(storeMock),
      close: storeMock.close.bind(storeMock),
      open: storeMock.open.bind(storeMock)
    }
  };
});

describe('Modal.svelte - keyboard and click behavior', () => {
  beforeEach(() => {
    // reset store between tests
    storeMock.set([]);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when store is empty', () => {
    render(Modal);
    // Should not find the dialog container
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('renders AlertModal when top entry type is alert', () => {
    render(Modal);
    storeMock.open({ id: 'a1', type: 'alert' });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByTestId('alert-modal')).toBeInTheDocument();
  });

  it('renders ConfirmModal when type is confirm', () => {
    render(Modal);
    storeMock.open({ id: 'c1', type: 'confirm' });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByTestId('confirm-modal')).toBeInTheDocument();
  });

  it('renders ComponentModal when type is component and component provided', () => {
    render(Modal);
    storeMock.open({ id: 'x1', type: 'component', component: {} });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByTestId('component-modal')).toBeInTheDocument();
  });

  it('Escape: prioritizes onCancel over onConfirm, then close()', async () => {
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    render(Modal);
    storeMock.open({ id: 'esc-1', type: 'confirm', onCancel, onConfirm });

    // Press Escape
    await userEvent.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onConfirm).not.toHaveBeenCalled();

    // Case: only onConfirm present
    storeMock.open({ id: 'esc-2', type: 'confirm', onConfirm });
    await userEvent.keyboard('{Escape}');
    expect(onConfirm).toHaveBeenCalledTimes(1);

    // Case: neither present -> calls store.close()
    const closeSpy = vi.spyOn(require('./modalStore.js'), 'modalStore', 'get');
    // Push a plain modal and press Escape
    storeMock.open({ id: 'esc-3', type: 'alert' });
    await userEvent.keyboard('{Escape}');
    // Validate last modal removed
    expect(storeMock.current.find((m) => m.id === 'esc-3')).toBeUndefined();
    closeSpy.mockRestore();
  });

  it('Enter: does nothing if target is INPUT/TEXTAREA/contentEditable', async () => {
    render(Modal);
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    storeMock.open({ id: 'enter-1', type: 'confirm', onCancel, onConfirm });

    // Send Enter from an input
    const input = document.createElement('input');
    document.body.appendChild(input);
    input.focus();
    await userEvent.keyboard('{Enter}');
    expect(onConfirm).not.toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();

    // Send Enter from a textarea
    const ta = document.createElement('textarea');
    document.body.appendChild(ta);
    ta.focus();
    await userEvent.keyboard('{Enter}');
    expect(onConfirm).not.toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();

    // Send Enter from a contentEditable element
    const ce = document.createElement('div');
    ce.contentEditable = 'true';
    document.body.appendChild(ce);
    ce.focus();
    await userEvent.keyboard('{Enter}');
    expect(onConfirm).not.toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();

    input.remove();
    ta.remove();
    ce.remove();
  });

  it('Enter: prioritizes onConfirm over onCancel, then close()', async () => {
    render(Modal);

    const onConfirm = vi.fn();
    const onCancel = vi.fn();

    storeMock.open({ id: 'enter-2', type: 'confirm', onConfirm, onCancel });
    await userEvent.keyboard('{Enter}');
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onCancel).not.toHaveBeenCalled();

    // Case: only onCancel present
    storeMock.open({ id: 'enter-3', type: 'confirm', onCancel });
    await userEvent.keyboard('{Enter}');
    expect(onCancel).toHaveBeenCalledTimes(1);

    // Case: neither present -> close
    storeMock.open({ id: 'enter-4', type: 'alert' });
    await userEvent.keyboard('{Enter}');
    expect(storeMock.current.find((m) => m.id === 'enter-4')).toBeUndefined();
  });

  it('Clicking the backdrop triggers onCancel, then onConfirm, else close()', async () => {
    render(Modal);

    const onCancel = vi.fn();
    const onConfirm = vi.fn();

    // 1) onCancel present
    storeMock.open({ id: 'click-1', type: 'confirm', onCancel, onConfirm });
    const dialog1 = screen.getByRole('dialog');

    await userEvent.click(dialog1); // click at overlay container (handler on wrapper)
    expect(onCancel).toHaveBeenCalledTimes(1);

    // 2) only onConfirm present
    storeMock.open({ id: 'click-2', type: 'confirm', onConfirm });
    const dialogs2 = screen.getAllByRole('dialog');
    const dialog2 = dialogs2[dialogs2.length - 1];
    await userEvent.click(dialog2);

    expect(onConfirm).toHaveBeenCalledTimes(1);

    // 3) none -> closes
    storeMock.open({ id: 'click-3', type: 'alert' });
    const dialogs3 = screen.getAllByRole('dialog');
    const dialog3 = dialogs3[dialogs3.length - 1];
    await userEvent.click(dialog3);
    expect(storeMock.current.find((m) => m.id === 'click-3')).toBeUndefined();
  });

  it('Click inside modal content does not propagate to outer click handler', async () => {
    render(Modal);
    const onCancel = vi.fn();
    storeMock.open({ id: 'stop-1', type: 'confirm', onCancel });

    // content region has role="region"
    const contentRegion = screen.getByRole('region');
    await userEvent.click(contentRegion);

    expect(onCancel).not.toHaveBeenCalled();
  });

  it('Handles multiple stacked modals: acts on the top-most only', async () => {
    render(Modal);

    const bottom = { id: 'stack-1', type: 'confirm', onCancel: vi.fn(), onConfirm: vi.fn() };
    const top = { id: 'stack-2', type: 'confirm', onCancel: vi.fn(), onConfirm: vi.fn() };

    storeMock.open(bottom);
    storeMock.open(top);

    // ESC affects top only
    await userEvent.keyboard('{Escape}');
    expect(top.onCancel).toHaveBeenCalledTimes(1);
    expect(bottom.onCancel).not.toHaveBeenCalled();

    // Another ESC should close or trigger again on remaining top (bottom becomes top)
    await userEvent.keyboard('{Escape}');
    expect(bottom.onCancel).toHaveBeenCalledTimes(1);
  });

  it('Does not crash if handlers are async', async () => {
    render(Modal);

    const onCancel = vi.fn(async () => {
      await new Promise((r) => setTimeout(r, 10));
    });

    storeMock.open({ id: 'async-1', type: 'confirm', onCancel });
    await userEvent.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(1);
  });
});

/*
Note on testing stack:
- Framework: Vitest
- Library: @testing-library/svelte (+ user-event)
These tests assert keyboard (Escape/Enter) and mouse interactions, rendering the correct subcomponent per modal type, click propagation behavior, and stack handling.
*/