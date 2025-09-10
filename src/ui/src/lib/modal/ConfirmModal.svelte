<script lang="ts">
	import { modalStore, type ModalSettings } from './modalStore.js';

	export let modal: ModalSettings & { id: string };

	function handleConfirm() {
		if (modal.onConfirm) {
			modal.onConfirm();
		} else {
			modalStore.close(modal.id);
		}
	}

	function handleCancel() {
		if (modal.onCancel) {
			modal.onCancel();
		} else {
			modalStore.close(modal.id);
		}
	}
</script>

<div class="card p-6 shadow-xl bg-surface-100-900 max-w-md w-full">
	{#if modal.title}
		<header id="modal-title-{modal.id}" class="text-xl font-bold mb-4">
			{modal.title}
		</header>
	{/if}

	{#if modal.body}
		<div class="mb-6">
			{modal.body}
		</div>
	{/if}

	<footer class="flex justify-end space-x-2">
		<button type="button" class="btn preset-tonal border border-surface-500" on:click={handleCancel}> Cancel </button>
		<button type="button" class="btn preset-filled-primary-500" on:click={handleConfirm}> Confirm </button>
	</footer>
</div>
