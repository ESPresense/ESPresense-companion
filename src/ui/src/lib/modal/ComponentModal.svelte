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
		buttonPositive: 'btn variant-filled-primary',
		buttonNeutral: 'btn variant-ghost',
		buttonTextCancel: 'Cancel'
	};
</script>

<div class="card p-6 shadow-xl bg-surface-100-800-token max-w-lg w-full max-h-[90vh] overflow-y-auto">
	{#if modal.component}
		<svelte:component this={modal.component} {parent} {...modal.props || {}} on:close={handleClose} />
	{/if}
</div>
