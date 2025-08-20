import { describe, it, expect, beforeEach } from 'vitest';
import { get } from 'svelte/store';
import { modalStore, type ModalSettings } from './modalStore';

describe('modalStore', () => {
  beforeEach(() => {
    modalStore.closeAll();
  });

  it('should start with no active modals', () => {
    const modals = get(modalStore);
    expect(modals).toHaveLength(0);
  });

  it('should open a modal', () => {
    const settings: ModalSettings = {
      type: 'alert',
      title: 'Test Modal',
      body: 'Test content'
    };

    const modalId = modalStore.open(settings);
    const modals = get(modalStore);

    expect(modals).toHaveLength(1);
    expect(modals[0].id).toBe(modalId);
    expect(modals[0].settings).toEqual(settings);
  });

  it('should close a modal by id', () => {
    const settings: ModalSettings = {
      type: 'alert',
      title: 'Test Modal'
    };

    const modalId = modalStore.open(settings);
    expect(get(modalStore)).toHaveLength(1);

    modalStore.close(modalId);
    expect(get(modalStore)).toHaveLength(0);
  });

  it('should close all modals', () => {
    modalStore.open({ type: 'alert', title: 'Modal 1' });
    modalStore.open({ type: 'alert', title: 'Modal 2' });
    modalStore.open({ type: 'alert', title: 'Modal 3' });

    expect(get(modalStore)).toHaveLength(3);

    modalStore.closeAll();
    expect(get(modalStore)).toHaveLength(0);
  });

  it('should generate unique modal IDs', () => {
    const modalId1 = modalStore.open({ type: 'alert' });
    const modalId2 = modalStore.open({ type: 'alert' });
    const modalId3 = modalStore.open({ type: 'alert' });

    expect(modalId1).not.toBe(modalId2);
    expect(modalId1).not.toBe(modalId3);
    expect(modalId2).not.toBe(modalId3);
  });

  it('should use custom modal ID when provided', () => {
    const customId = 'custom-modal-id';
    const settings: ModalSettings = {
      id: customId,
      type: 'alert',
      title: 'Custom ID Modal'
    };

    modalStore.open(settings);
    const modals = get(modalStore);

    expect(modals).toHaveLength(1);
    expect(modals[0].id).toBe(customId);
  });
});