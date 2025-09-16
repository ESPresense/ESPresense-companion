<script lang="ts">
	import { toastStore, type ToastSettings } from './toastStore.js';
	import { fly } from 'svelte/transition';
	import { createEventDispatcher } from 'svelte';

	const dispatch = createEventDispatcher();

	function handleClose(id: string) {
		toastStore.close(id);
	}

	function handleAction(toast: ToastSettings & { id: string }) {
		if (toast.action) {
			toast.action.response();
		}
		handleClose(toast.id);
	}

	// Map Skeleton background classes to appropriate styles
	function getToastClasses(background?: string): string {
		const baseClasses = 'toast-item card p-4 shadow-lg max-w-md w-full flex items-center justify-between';

		switch (background) {
			case 'preset-filled-success-500':
				return `${baseClasses} bg-success-500 text-on-success-token`;
			case 'preset-filled-error-500':
				return `${baseClasses} bg-error-500 text-on-error-token`;
			case 'preset-filled-warning-500':
				return `${baseClasses} bg-warning-500 text-on-warning-token`;
			case 'preset-filled-primary-500':
				return `${baseClasses} bg-primary-500 text-on-primary-token`;
			case 'preset-filled-secondary-500':
				return `${baseClasses} bg-secondary-500 text-on-secondary-token`;
			default:
				return `${baseClasses} bg-surface-100-800-token`;
		}
	}
</script>

<div class="toast-container fixed top-4 right-4 z-50 space-y-2">
	{#each $toastStore as toast (toast.id)}
		<div class={getToastClasses(toast.background)} in:fly={{ x: 300, duration: 300 }} out:fly={{ x: 300, duration: 200 }}>
			<div class="flex-1">
				{toast.message}
			</div>

			<div class="flex items-center space-x-2 ml-4">
				{#if toast.action}
					<button type="button" class="btn btn-sm preset-tonal-surface border border-surface-500" onclick={() => handleAction(toast)}>
						{toast.action.label}
					</button>
				{/if}

				<button type="button" class="btn-icon btn-icon-sm preset-tonal-surface border border-surface-500" onclick={() => handleClose(toast.id)} aria-label="Close toast">
					<svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
						<path fill-rule="evenodd" d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z" clip-rule="evenodd"></path>
					</svg>
				</button>
			</div>
		</div>
	{/each}
</div>

<style>
	.toast-container {
		pointer-events: none;
	}

	.toast-item {
		pointer-events: auto;
	}
</style>
