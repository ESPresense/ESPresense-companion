<script lang="ts">
	import { modalStore, type ModalSettings } from './modalStore.js';

	export let modal: ModalSettings & { id: string };

	function handleClose() {
		modalStore.close(modal.id);
	}

	// Create a parent-like object for backward compatibility with existing modal components
	const parent = {
		onClose: handleClose,
		regionFooter: 'flex justify-end space-x-2',
		buttonPositive: 'btn preset-filled-primary-500',
		buttonNeutral: 'btn preset-tonal border border-surface-500',
		buttonTextCancel: 'Cancel'
	};
</script>

<div class="card bg-surface-50-950 p-6 border border-surface-500 shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto">
	{#if modal.component}
		<svelte:component this={modal.component} {parent} {...modal.props || {}} onclose={handleClose} />
	{/if}
</div>
