import { modalStore } from './modalStore';
import AlertModal from './modals/AlertModal.svelte';
import ConfirmModal from './modals/ConfirmModal.svelte';

export function showAlert(options: {
	title?: string;
	message: string;
	type?: 'info' | 'warning' | 'error' | 'success';
}) {
	return modalStore.open({
		type: 'component',
		component: AlertModal,
		props: {
			title: options.title,
			message: options.message,
			type: options.type || 'info'
		}
	});
}

export function showConfirm(options: {
	title?: string;
	message: string;
	confirmText?: string;
	cancelText?: string;
	type?: 'info' | 'warning' | 'error' | 'success';
}) {
	return new Promise<boolean>((resolve) => {
		const modalId = modalStore.open({
			type: 'component',
			component: ConfirmModal,
			props: {
				title: options.title,
				message: options.message,
				confirmText: options.confirmText,
				cancelText: options.cancelText,
				type: options.type || 'warning'
			}
		});

		// Listen for confirm/cancel events
		let unsubscribe: () => void;
		unsubscribe = modalStore.subscribe((modals) => {
			const modalExists = modals.some(m => m.id === modalId);
			if (!modalExists) {
				// Modal was closed, resolve false (cancelled)
				resolve(false);
				unsubscribe();
			}
		});

		// We would need a way to listen for component events here
		// For now, this is a simplified version
	});
}

export function showCustomModal(component: any, props: Record<string, any> = {}) {
	return modalStore.open({
		type: 'component',
		component,
		props
	});
}

export function closeModal(id: string) {
	modalStore.close(id);
}

export function closeAllModals() {
	modalStore.closeAll();
}