import { getContext } from 'svelte';

/**
 * Get the toast store from context
 */
export function getToastStore() {
	const toastStore = getContext('toast');
	if (!toastStore) {
		// Return a mock toast store if not found to prevent errors
		return {
			create: (settings: any) => {
				console.warn('ToastStore not properly initialized. Toast message:', settings);
			}
		};
	}
	return toastStore;
}

/**
 * Helper function to create a simple modal store interface
 * For full modal functionality, use the Modal component directly
 */
export function getModalStore() {
	return {
		trigger: (settings: any) => {
			// For now, use browser confirm for simple confirmations
			// In a full implementation, you'd want to use the Modal component
			if (settings.type === 'confirm') {
				const result = confirm(settings.body);
				if (settings.response) {
					settings.response(result);
				}
				return result;
			}
			console.warn('getModalStore is deprecated. Use Modal component directly.');
			return Promise.resolve(false);
		}
	};
}

/**
 * Simple popup action for basic tooltip functionality
 * For full popup functionality, use Tooltip or Popover components
 */
export function popup(node: HTMLElement, params: any) {
	// Basic tooltip implementation
	if (params.event === 'hover') {
		const showTooltip = () => {
			node.title = params.target || '';
		};
		
		const hideTooltip = () => {
			node.title = '';
		};
		
		node.addEventListener('mouseenter', showTooltip);
		node.addEventListener('mouseleave', hideTooltip);
		
		return {
			destroy() {
				node.removeEventListener('mouseenter', showTooltip);
				node.removeEventListener('mouseleave', hideTooltip);
			}
		};
	}
	
	return {
		destroy() {}
	};
}

// Legacy types for backward compatibility
export type ToastSettings = {
	message?: string;
	title?: string;
	description?: string;
	type?: 'info' | 'error' | 'success';
	background?: string;
	duration?: number;
};

export type ModalStore = ReturnType<typeof getModalStore>;