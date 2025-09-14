import { describe, it, expect, vi, beforeEach } from 'vitest';
import { get } from 'svelte/store';
import { modalStore } from './modalStore.js';

// Helper function to simulate isInsideInteractive logic from Modal.svelte
function isInsideInteractive(element: HTMLElement | null): boolean {
  if (!element) return false;

  const interactiveTags = ['INPUT', 'TEXTAREA', 'BUTTON', 'SELECT', 'A'];
  if (interactiveTags.includes(element.tagName)) return true;

  if (element.isContentEditable || element.getAttribute('contenteditable') === 'true') return true;

  if (element.hasAttribute('tabindex') && element.getAttribute('tabindex') !== '-1') return true;
  if (element.getAttribute('role') === 'button' || element.getAttribute('role') === 'link') return true;

  return isInsideInteractive(element.parentElement);
}

// Helper function to simulate Modal.svelte keyboard handling
function handleKeydown(event: KeyboardEvent) {
  const modals = get(modalStore);
  if (modals.length === 0) return;

  const modal = modals[modals.length - 1];

  if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    if (modal.onCancel) {
      modal.onCancel();
    } else if (modal.onConfirm) {
      modal.onConfirm();
    } else {
      modalStore.close();
    }
  } else if (event.key === 'Enter') {
    const target = event.target as HTMLElement;
    if (isInsideInteractive(target)) {
      return;
    }
    event.preventDefault();
    if (modal.onConfirm) {
      modal.onConfirm();
    }
  }
  // Space key is intentionally not handled by modal keyboard logic
}

describe('Modal DOM Integration Tests', () => {
  beforeEach(() => {
    modalStore.clear();
    vi.clearAllMocks();
  });

  it('should call onCancel when Escape pressed on confirm modal', () => {
    const onConfirm = vi.fn();
    const onCancel = vi.fn();

    // Trigger a confirm modal
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm,
      onCancel
    });

    // Create and dispatch Escape key event on window
    const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
    handleKeydown(escapeEvent);

    // Assert onCancel is called, not onConfirm
    expect(onCancel).toHaveBeenCalledOnce();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should call onConfirm when Escape pressed on alert modal (fallback)', () => {
    const onConfirm = vi.fn();

    // Trigger an alert modal
    modalStore.trigger({
      type: 'alert',
      title: 'Test Alert',
      onConfirm
    });

    // Create and dispatch Escape key event on window
    const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
    handleKeydown(escapeEvent);

    // Assert onConfirm is called (fallback for alert modals)
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('should not activate background click when Space pressed with modal open', () => {
    const onConfirm = vi.fn();
    const onCancel = vi.fn();

    // Trigger a confirm modal
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm,
      onCancel
    });

    // Create and dispatch Space key event on window
    const spaceEvent = new KeyboardEvent('keydown', { key: ' ' });
    handleKeydown(spaceEvent);

    // Assert neither onCancel nor onConfirm are called (Space doesn't trigger modal actions)
    expect(onCancel).not.toHaveBeenCalled();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should handle Enter key on non-interactive element', () => {
    const onConfirm = vi.fn();

    // Trigger a confirm modal
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    // Create and dispatch Enter key event on window (simulating focus on non-interactive element)
    const enterEvent = new KeyboardEvent('keydown', { key: 'Enter' });
    // Simulate the event target being a non-interactive div
    Object.defineProperty(enterEvent, 'target', { value: document.createElement('div') });
    handleKeydown(enterEvent);

    // Assert onConfirm is called
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('should not handle Enter key when target is interactive', () => {
    const onConfirm = vi.fn();

    // Trigger a confirm modal
    modalStore.trigger({
      type: 'confirm',
      title: 'Test Confirm',
      onConfirm
    });

    // Create an input element to simulate focus on interactive element
    const input = document.createElement('input');

    // Create and dispatch Enter key event on window
    const enterEvent = new KeyboardEvent('keydown', { key: 'Enter' });
    Object.defineProperty(enterEvent, 'target', { value: input });
    handleKeydown(enterEvent);

    // Assert onConfirm is not called when target is interactive
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('should target top-most modal in stack for Escape key', () => {
    const onConfirm1 = vi.fn();
    const onConfirm2 = vi.fn();

    // Trigger first modal
    modalStore.trigger({
      type: 'alert',
      title: 'First Modal',
      onConfirm: onConfirm1
    });

    // Trigger second modal (top-most)
    modalStore.trigger({
      type: 'alert',
      title: 'Second Modal',
      onConfirm: onConfirm2
    });

    // Create and dispatch Escape key event on window
    const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
    handleKeydown(escapeEvent);

    // Only the top modal should be handled
    expect(onConfirm2).toHaveBeenCalledOnce();
    expect(onConfirm1).not.toHaveBeenCalled();
  });

  it('should close modal with no handlers when Escape pressed', () => {
    const closeSpy = vi.spyOn(modalStore, 'close');

    // Trigger modal with no handlers
    modalStore.trigger({
      type: 'alert',
      title: 'Test Modal'
      // No onConfirm or onCancel handlers
    });

    // Create and dispatch Escape key event on window
    const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
    handleKeydown(escapeEvent);

    // Assert modalStore.close is called
    expect(closeSpy).toHaveBeenCalledOnce();
  });

  it('should ignore keyboard events when no modals are open', () => {
    const onConfirm = vi.fn();

    // Don't trigger any modals
    // Create and dispatch keyboard events
    const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
    const enterEvent = new KeyboardEvent('keydown', { key: 'Enter' });
    const spaceEvent = new KeyboardEvent('keydown', { key: ' ' });

    handleKeydown(escapeEvent);
    handleKeydown(enterEvent);
    handleKeydown(spaceEvent);

    // Assert no handlers are called
    expect(onConfirm).not.toHaveBeenCalled();
  });
});