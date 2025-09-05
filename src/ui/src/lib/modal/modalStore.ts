import { writable, derived, get } from 'svelte/store';
// Using any for component type to avoid complex TypeScript issues
type ComponentType = any;

export interface ModalSettings {
	id?: string;
	type: 'alert' | 'confirm' | 'component';
	title?: string;
	body?: string;
	component?: ComponentType;
	props?: Record<string, any>;
	onConfirm?: () => void | Promise<void>;
	onCancel?: () => void | Promise<void>;
}

interface Modal extends ModalSettings {
	id: string;
}

function createModalStore() {
	const { subscribe, set, update } = writable<Modal[]>([]);

	let idCounter = 0;

	function generateId(): string {
		return `modal-${++idCounter}-${Date.now()}`;
	}

	function trigger(settings: ModalSettings): string {
		const id = settings.id || generateId();
		const modal: Modal = { ...settings, id };

		update((modals) => [...modals, modal]);
		return id;
	}

	function close(id?: string) {
		if (id) {
			update((modals) => modals.filter((m) => m.id !== id));
		} else {
			update((modals) => modals.slice(0, -1));
		}
	}

	function clear() {
		set([]);
	}

	return {
		subscribe,
		trigger,
		close,
		clear
	};
}

export const modalStore = createModalStore();

// Helper functions for common modal types
export function showAlert(settings: Omit<ModalSettings, 'type'>): Promise<void> {
	return new Promise((resolve) => {
		const id = modalStore.trigger({
			...settings,
			type: 'alert',
			onConfirm: () => {
				modalStore.close(id);
				resolve();
			}
		});
	});
}

export function showConfirm(settings: Omit<ModalSettings, 'type'>): Promise<boolean> {
	return new Promise((resolve) => {
		const id = modalStore.trigger({
			...settings,
			type: 'confirm',
			onConfirm: () => {
				modalStore.close(id);
				resolve(true);
			},
			onCancel: () => {
				modalStore.close(id);
				resolve(false);
			}
		});
	});
}

export function showComponent(component: ComponentType, props?: Record<string, any>): string {
	return modalStore.trigger({
		type: 'component',
		component,
		props
	});
}

// Compatibility functions for migration from Skeleton
export function getModalStore() {
	return modalStore;
}
