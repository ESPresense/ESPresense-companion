<script lang="ts">
	import { modalStore } from './modalStore.js';
	import ConfirmModal from './ConfirmModal.svelte';
	import AlertModal from './AlertModal.svelte';
	import ComponentModal from './ComponentModal.svelte';

	function isInsideInteractive(element: HTMLElement | null): boolean {
		if (!element) return false;
		
		// Check if the element itself is interactive
		const interactiveTags = ['INPUT', 'TEXTAREA', 'BUTTON', 'SELECT', 'A'];
		if (interactiveTags.includes(element.tagName)) return true;
		
		// Check if the element is contentEditable
		if (element.isContentEditable) return true;
		
		// Check if the element has interactive attributes
		if (element.hasAttribute('tabindex') && element.getAttribute('tabindex') !== '-1') return true;
		if (element.getAttribute('role') === 'button' || element.getAttribute('role') === 'link') return true;
		
		// Check parent elements up the tree
		return isInsideInteractive(element.parentElement);
	}

	async function handleKeydown(event: KeyboardEvent) {
		if ($modalStore.length === 0) return;

		const modal = $modalStore[$modalStore.length - 1];

		if (event.key === 'Escape') {
			event.preventDefault();
			event.stopPropagation();
			if (modal.onCancel) {
				await modal.onCancel();
			} else if (modal.onConfirm) {
				await modal.onConfirm();
			} else {
				modalStore.close();
			}
		} else if (event.key === 'Enter') {
			const target = event.target as HTMLElement;
			if (isInsideInteractive(target)) {
				return;
			}
			event.preventDefault();
			if (modal.onConfirm) {
				await modal.onConfirm();
			}
		}
	}
</script>

<svelte:window on:keydown={handleKeydown} />

{#each $modalStore as modal (modal.id)}
	<div
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
		role="dialog"
		aria-modal="true"
		aria-labelledby="modal-title-{modal.id}"
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
