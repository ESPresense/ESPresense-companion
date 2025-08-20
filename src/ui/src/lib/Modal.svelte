<script lang="ts">
	import { createEventDispatcher } from 'svelte';
	import { fade, fly } from 'svelte/transition';

	const dispatch = createEventDispatcher();

	export let open = false;
	export let zIndex = 9999;

	function handleClose() {
		open = false;
		dispatch('close');
	}

	function handleBackdropClick(event: MouseEvent) {
		if (event.target === event.currentTarget) {
			handleClose();
		}
	}

	// Close modal on escape key
	function handleKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape' && open) {
			handleClose();
		}
	}
</script>

<svelte:window on:keydown={handleKeydown} />

{#if open}
	<!-- Backdrop -->
	<div
		class="fixed inset-0 bg-black/50 backdrop-blur-sm z-{zIndex}"
		on:click={handleBackdropClick}
		on:keydown={(e) => e.key === 'Enter' && handleClose()}
		role="button"
		tabindex="0"
		in:fade={{ duration: 150 }}
		out:fade={{ duration: 150 }}
	>
		<!-- Modal Content -->
		<div
			class="fixed inset-0 z-{zIndex + 1} flex items-center justify-center p-4"
		>
			<div
				class="card p-6 shadow-2xl bg-surface-100-900-token max-w-md w-full max-h-[90vh] overflow-y-auto"
				in:fly={{ y: 50, duration: 200 }}
				out:fly={{ y: 50, duration: 200 }}
			>
				<slot {handleClose} />
			</div>
		</div>
	</div>
{/if}