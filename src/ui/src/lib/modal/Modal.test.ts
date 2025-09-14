import { describe, it, expect, vi, beforeEach } from 'vitest';
import { get } from 'svelte/store';
import { modalStore } from './modalStore.js';

// Test the keyboard handling logic without rendering the full component
describe('Modal Keyboard Handler Logic', () => {
  beforeEach(() => {
    modalStore.clear();
    vi.clearAllMocks();
  });

  // Test the isInsideInteractive helper function logic
  function isInsideInteractive(element: HTMLElement | null): boolean {
    if (!element) return false;
    
    const interactiveTags = ['INPUT', 'TEXTAREA', 'BUTTON', 'SELECT', 'A'];
    if (interactiveTags.includes(element.tagName)) return true;
    
    if (element.isContentEditable || element.getAttribute('contenteditable') === 'true') return true;
    
    if (element.hasAttribute('tabindex') && element.getAttribute('tabindex') !== '-1') return true;
    if (element.getAttribute('role') === 'button' || element.getAttribute('role') === 'link') return true;
    
    return isInsideInteractive(element.parentElement);
  }

  // Mock keyboard handler that replicates Modal.svelte logic
  async function mockKeyboardHandler(event: Pick<KeyboardEvent, 'key' | 'target'>) {
    const modals = get(modalStore);
    if (modals.length === 0) return;

    const modal = modals[modals.length - 1];

    if (event.key === 'Escape') {
      if (modal.onCancel) {
        await modal.onCancel();
      } else if (modal.onConfirm) {
        await modal.onConfirm();
      } else {
        modalStore.close();
      }
    } else if (event.key === 'Enter') {
      const target = event.target as HTMLElement;
      if (isInsideInteractive(target)) {
        return;
      }
      if (modal.onConfirm) {
        await modal.onConfirm();
      }
    }
  }

  it('should identify interactive elements correctly', () => {
    // Create test elements
    const input = document.createElement('input');
    const button = document.createElement('button');
    const div = document.createElement('div');
    const contentEditable = document.createElement('div');
    contentEditable.setAttribute('contenteditable', 'true');
    
    // Test interactive elements
    expect(isInsideInteractive(input)).toBe(true);
    expect(isInsideInteractive(button)).toBe(true);
    expect(isInsideInteractive(contentEditable)).toBe(true);
    
    // Test non-interactive element
    expect(isInsideInteractive(div)).toBe(false);
    
    // Test role-based interactive elements
    div.setAttribute('role', 'button');
    expect(isInsideInteractive(div)).toBe(true);
    
    div.removeAttribute('role');
    div.setAttribute('tabindex', '0');
    expect(isInsideInteractive(div)).toBe(true);
    
    // Test null
    expect(isInsideInteractive(null)).toBe(false);
  });

  it('should handle Escape key with onCancel priority', async () => {
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm,
      onCancel
    });

    await mockKeyboardHandler({ key: 'Escape', target: document.body });
    
    expect(onCancel).toHaveBeenCalledOnce();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should handle Escape key with onConfirm fallback', async () => {
    const onConfirm = vi.fn();
    
    modalStore.trigger({
      type: 'alert',
      title: 'Test Alert',
      onConfirm
    });

    await mockKeyboardHandler({ key: 'Escape', target: document.body });
    
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('should handle Enter key on non-interactive element', async () => {
    const onConfirm = vi.fn();
    
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    const div = document.createElement('div');
    await mockKeyboardHandler({ key: 'Enter', target: div });
    
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('should NOT handle Enter key on interactive element', async () => {
    const onConfirm = vi.fn();
    
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    const input = document.createElement('input');
    await mockKeyboardHandler({ key: 'Enter', target: input });
    
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should NOT handle Enter key on contentEditable element', async () => {
    const onConfirm = vi.fn();
    
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    const contentEditable = document.createElement('div');
    contentEditable.setAttribute('contenteditable', 'true');
    await mockKeyboardHandler({ key: 'Enter', target: contentEditable });
    
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should NOT handle Enter key on element with role="button"', async () => {
    const onConfirm = vi.fn();
    
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    const roleButton = document.createElement('div');
    roleButton.setAttribute('role', 'button');
    await mockKeyboardHandler({ key: 'Enter', target: roleButton });
    
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should target top-most modal in stack', async () => {
    const onConfirm1 = vi.fn();
    const onConfirm2 = vi.fn();
    
    modalStore.trigger({
      type: 'alert',
      title: 'First Modal',
      onConfirm: onConfirm1
    });

    modalStore.trigger({
      type: 'alert',
      title: 'Second Modal',
      onConfirm: onConfirm2
    });

    await mockKeyboardHandler({ key: 'Escape', target: document.body });
    
    // Only the top modal should be handled
    expect(onConfirm2).toHaveBeenCalledOnce();
    expect(onConfirm1).not.toHaveBeenCalled();
  });

  it('should close modal when no handlers provided', async () => {
    const closeSpy = vi.spyOn(modalStore, 'close');
    
    modalStore.trigger({
      type: 'alert',
      title: 'Test Modal'
      // No onConfirm or onCancel handlers
    });

    await mockKeyboardHandler({ key: 'Escape', target: document.body });
    
    expect(closeSpy).toHaveBeenCalledOnce();
  });

  it('should ignore keyboard events when no modals are open', async () => {
    const onConfirm = vi.fn();
    
    // Don't trigger any modals
    await mockKeyboardHandler({ key: 'Escape', target: document.body });
    await mockKeyboardHandler({ key: 'Enter', target: document.body });
    
    expect(onConfirm).not.toHaveBeenCalled();
  });
});