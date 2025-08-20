import { describe, it, expect, beforeEach, vi } from 'vitest';
import { get } from 'svelte/store';
import { modalStore } from './modalStore';
import { showAlert, showConfirm, showCustomModal, closeModal, closeAllModals } from './modalUtils';

describe('modalUtils', () => {
  beforeEach(() => {
    modalStore.closeAll();
  });

  describe('showAlert', () => {
    it('should open an alert modal', () => {
      const options = {
        title: 'Test Alert',
        message: 'This is a test message',
        type: 'info' as const
      };

      const modalId = showAlert(options);
      const modals = get(modalStore);

      expect(modals).toHaveLength(1);
      expect(modals[0].id).toBe(modalId);
      expect(modals[0].settings.type).toBe('component');
      expect(modals[0].settings.props).toEqual(options);
    });
  });

  describe('showConfirm', () => {
    it('should open a confirm modal', () => {
      const options = {
        title: 'Test Confirm',
        message: 'Are you sure?',
        type: 'warning' as const
      };

      const result = showConfirm(options);
      const modals = get(modalStore);

      // showConfirm returns a Promise, but should still open a modal
      expect(modals).toHaveLength(1);
      expect(modals[0].settings.type).toBe('component');
      expect(modals[0].settings.props).toEqual(options);
      expect(result).toBeInstanceOf(Promise);
    });
  });

  describe('showCustomModal', () => {
    it('should open a custom modal component', () => {
      const TestComponent = vi.fn();
      const props = { testProp: 'testValue' };

      const modalId = showCustomModal(TestComponent, props);
      const modals = get(modalStore);

      expect(modals).toHaveLength(1);
      expect(modals[0].id).toBe(modalId);
      expect(modals[0].settings.type).toBe('component');
      expect(modals[0].settings.component).toBe(TestComponent);
      expect(modals[0].settings.props).toEqual(props);
    });
  });

  describe('closeModal', () => {
    it('should close a modal by id', () => {
      const modalId = showAlert({ message: 'Test' });
      expect(get(modalStore)).toHaveLength(1);

      closeModal(modalId);
      expect(get(modalStore)).toHaveLength(0);
    });
  });

  describe('closeAllModals', () => {
    it('should close all modals', () => {
      showAlert({ message: 'Alert 1' });
      showAlert({ message: 'Alert 2' });
      showAlert({ message: 'Alert 3' });

      expect(get(modalStore)).toHaveLength(3);

      closeAllModals();
      expect(get(modalStore)).toHaveLength(0);
    });
  });
});