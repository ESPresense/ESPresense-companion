import { writable } from 'svelte/store';

export interface ModalSettings {
	id?: string;
	type: 'alert' | 'confirm' | 'component';
	title?: string;
	body?: string;
	component?: any;
	props?: Record<string, any>;
	onConfirm?: () => void;
	onCancel?: () => void;
}

export interface ActiveModal {
	id: string;
	settings: ModalSettings;
}

function createModalStore() {
	const { subscribe, set, update } = writable<ActiveModal[]>([]);

	return {
		subscribe,
		open: (settings: ModalSettings) => {
			const id = settings.id || Math.random().toString(36).substr(2, 9);
			const modal: ActiveModal = { id, settings };

			update(modals => [...modals, modal]);
			return id;
		},
		close: (id: string) => {
			update(modals => modals.filter(m => m.id !== id));
		},
		closeAll: () => {
			set([]);
		}
	};
}

export const modalStore = createModalStore();