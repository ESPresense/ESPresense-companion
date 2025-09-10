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

/**
 * Display a success toast and return its id.
 *
 * Triggers a toast with the provided `message` and a success background token (`preset-filled-success-500`).
 *
 * @param options - Optional toast settings other than `message` and `background` (e.g., `timeout`, `autohide`, `action`, `id`).
 * @returns The id of the created toast.
 */
export function showSuccess(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'preset-filled-success-500'
	});
}

/**
 * Show an error toast with a preset error background and return its id.
 *
 * The toast's `background` is set to the token `'preset-filled-error-500'`.
 *
 * @param message - Text to display in the toast
 * @param options - Optional additional toast settings (e.g., `id`, `timeout`, `autohide`, `action`)
 * @returns The toast id
 */
export function showError(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'preset-filled-error-500'
	});
}

/**
 * Show an informational toast and return its id.
 *
 * Triggers a toast with the given `message` and a primary info background token (`preset-filled-primary-500`).
 *
 * @param message - Text to display in the toast.
 * @param options - Optional toast settings (e.g., `timeout`, `autohide`, `action`, `id`). `message` and `background` are not accepted here.
 * @returns The id of the created toast.
 */
export function showInfo(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'preset-filled-primary-500'
	});
}

/**
 * Show a warning toast with the given message.
 *
 * The toast's background is set to the warning token `preset-filled-warning-500`.
 *
 * @param message - Text to display in the toast.
 * @param options - Additional ToastSettings (excluding `message` and `background`) to apply.
 * @returns The id of the created toast.
 */
export function showWarning(message: string, options?: Omit<ToastSettings, 'message' | 'background'>): string {
	return toastStore.trigger({
		...options,
		message,
		background: 'preset-filled-warning-500'
	});
}

/**
 * Returns the module's toast store instance.
 *
 * Provided for migration compatibility (returns the same `toastStore` exported by this module).
 *
 * @returns The toast store instance used for subscribing and managing toasts.
 */
export function getToastStore() {
	return toastStore;
}
