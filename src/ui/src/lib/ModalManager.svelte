<script lang="ts">
	import { onDestroy } from 'svelte';
	import { modalStore, type ActiveModal, type ModalSettings } from './modalStore';
	import { fade, fly } from 'svelte/transition';

	let activeModals: ActiveModal[] = [];

	const unsubscribe = modalStore.subscribe(modals => {
		activeModals = modals;
	});

	onDestroy(() => {
		unsubscribe();
	});

	function handleClose(id: string) {
		modalStore.close(id);
	}

	function handleBackdropClick(event: MouseEvent, id: string) {
		if (event.target === event.currentTarget) {
			handleClose(id);
		}
	}

	function handleKeydown(event: KeyboardEvent, id: string) {
		if (event.key === 'Escape') {
			handleClose(id);
		}
	}

	function handleConfirm(settings: ModalSettings, id: string) {
		if (settings.onConfirm) {
			settings.onConfirm();
		}
		handleClose(id);
	}

	function handleCancel(settings: ModalSettings, id: string) {
		if (settings.onCancel) {
			settings.onCancel();
		}
		handleClose(id);
	}
</script>

<svelte:window on:keydown={(e) => activeModals.length > 0 && handleKeydown(e, activeModals[activeModals.length - 1].id)} />

{#each activeModals as modal (modal.id)}
	{@const settings = modal.settings}
	<!-- Backdrop -->
	<div
		class="fixed inset-0 bg-black/50 backdrop-blur-sm z-9999"
		on:click={(e) => handleBackdropClick(e, modal.id)}
		in:fade={{ duration: 150 }}
		out:fade={{ duration: 150 }}
	>
		<!-- Modal Content -->
		<div
			class="fixed inset-0 z-10000 flex items-center justify-center p-4"
		>
			<div
				class="card p-6 shadow-2xl bg-surface-100-900-token max-w-md w-full max-h-[90vh] overflow-y-auto"
				in:fly={{ y: 20, duration: 200 }}
				out:fly={{ y: 20, duration: 200 }}
			>
				{#if settings.type === 'component' && settings.component}
					<svelte:component
						this={settings.component}
						{...settings.props}
						on:close={() => handleClose(modal.id)}
					/>
				{:else}
					<!-- Header -->
					{#if settings.title}
						<header class="mb-4">
							<h3 class="text-xl font-bold">{settings.title}</h3>
						</header>
					{/if}

					<!-- Body -->
					{#if settings.body}
						<article class="mb-6">
							<p>{settings.body}</p>
						</article>
					{/if}

					<!-- Actions -->
					<footer class="flex justify-end space-x-2">
						{#if settings.type === 'confirm'}
							<button
								class="btn btn-ghost"
								on:click={() => handleCancel(settings, modal.id)}
							>
								Cancel
							</button>
							<button
								class="btn btn-primary"
								on:click={() => handleConfirm(settings, modal.id)}
							>
								Confirm
							</button>
						{:else}
							<button
								class="btn btn-primary"
								on:click={() => handleClose(modal.id)}
							>
								Close
							</button>
						{/if}
					</footer>
				{/if}
			</div>
		</div>
	</div>
{/each}