<script lang="ts">
	import { modalStore } from './modalStore.js';
	import ConfirmModal from './ConfirmModal.svelte';
	import AlertModal from './AlertModal.svelte';
	import ComponentModal from './ComponentModal.svelte';

	let modalBackdrop: HTMLDivElement | null = null;

	// Focus the modal backdrop when it opens to capture keyboard events
	$: if ($modalStore.length > 0 && modalBackdrop) {
		modalBackdrop!.focus();
	}

	async function handleKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape' && $modalStore.length > 0) {
			event.preventDefault();
			event.stopPropagation();
			const modal = $modalStore[$modalStore.length - 1];
			if (modal.onCancel) {
				await modal.onCancel();
			} else if (modal.onConfirm) {
				await modal.onConfirm();
			} else {
				modalStore.close();
			}
		}
	}
</script>

<svelte:window on:keydown={handleKeydown} />

{#each $modalStore as modal (modal.id)}
	<div
		bind:this={modalBackdrop}
		class="fixed inset-0 z-50 flex items-center justify-center p-4"
		on:click={async () => {
			if (modal.onCancel) {
				await modal.onCancel();
			} else if (modal.onConfirm) {
				await modal.onConfirm();
			} else {
				modalStore.close();
			}
		}}
		on:keydown={async (e) => {
			if (e.key === 'Enter' || e.key === ' ') {
				e.preventDefault();
				if (modal.onConfirm) {
					await modal.onConfirm();
				} else if (modal.onCancel) {
					await modal.onCancel();
				} else {
					modalStore.close();
				}
			}
		}}
		role="dialog"
		aria-modal="true"
		aria-labelledby="modal-title-{modal.id}"
		tabindex="-1"
	>
		<!-- Backdrop -->
		<div class="absolute inset-0 bg-black/50 backdrop-blur-sm"></div>

		<!-- Modal Content -->
		<div class="relative z-10 max-w-lg w-full" on:click|stopPropagation aria-hidden="false" role="region">
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
