import { writable, derived } from 'svelte/store';

export interface ToastSettings {
	id?: string;
	message: string;
	background?: string;
	timeout?: number;
	autohide?: boolean;
	action?: {
		label: string;
		response: () => void;
	};
}

interface Toast extends ToastSettings {
	id: string;
	timestamp: number;
}

function createToastStore() {
	const { subscribe, set, update } = writable<Toast[]>([]);

	let idCounter = 0;

	function generateId(): string {
		return `toast-${++idCounter}-${Date.now()}`;
	}

	function trigger(settings: ToastSettings): string {
		const id = settings.id || generateId();
		const toast: Toast = {
			...settings,
			id,
			timestamp: Date.now(),
			timeout: settings.timeout ?? 5000,
			autohide: settings.autohide ?? true
		};

		update((toasts) => [...toasts, toast]);

		// Auto-remove toast if autohide is enabled
		if (toast.autohide && toast.timeout && toast.timeout > 0) {
			setTimeout(() => {
				close(id);
			}, toast.timeout);
		}

		return id;
	}

	function close(id: string) {
		update((toasts) => toasts.filter((t) => t.id !== id));
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

export const toastStore = createToastStore();

// Helper functions for common toast types
export function showSuccess(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'variant-filled-success'
	});
}

export function showError(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'variant-filled-error'
	});
}

export function showInfo(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'variant-filled-primary'
	});
}

export function showWarning(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'variant-filled-warning'
	});
}

// Compatibility function for migration from Skeleton
export function getToastStore() {
	return toastStore;
}