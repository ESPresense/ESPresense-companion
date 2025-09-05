<script lang="ts">
	import { modalStore } from './modalStore.js';
	import ConfirmModal from './ConfirmModal.svelte';
	import AlertModal from './AlertModal.svelte';
	import ComponentModal from './ComponentModal.svelte';
	import { createEventDispatcher } from 'svelte';

	const dispatch = createEventDispatcher();

	function handleKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape' && $modalStore.length > 0) {
			event.preventDefault();
			event.stopPropagation();
			modalStore.close();
		}
	}

	function handleBackdropClick(event: MouseEvent) {
		if (event.target === event.currentTarget) {
			modalStore.close();
		}
	}
</script>

<svelte:window on:keydown={handleKeydown} />

{#each $modalStore as modal (modal.id)}
	<div
		class="fixed inset-0 z-50 flex items-center justify-center p-4"
		on:click={handleBackdropClick}
		role="dialog"
		aria-modal="true"
		aria-labelledby="modal-title-{modal.id}"
	>
		<!-- Backdrop -->
		<div class="absolute inset-0 bg-black/50 backdrop-blur-sm"></div>

		<!-- Modal Content -->
		<div class="relative z-10 max-w-lg w-full">
			{#if modal.type === 'alert'}
				<AlertModal {modal} />
			{:else if modal.type === 'confirm'}
				<ConfirmModal {modal} />
			{:else if modal.type === 'component' && modal.component}
				<ComponentModal {modal} />
			{/if}
		</div>
	</div>
{/each}
