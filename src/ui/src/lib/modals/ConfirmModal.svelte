<script lang="ts">
	import { createEventDispatcher } from 'svelte';

	const dispatch = createEventDispatcher();

	export let title = 'Confirm';
	export let message = 'Are you sure?';
	export let confirmText = 'Confirm';
	export let cancelText = 'Cancel';
	export let type: 'info' | 'warning' | 'error' | 'success' = 'warning';

	function handleConfirm() {
		dispatch('confirm');
		dispatch('close');
	}

	function handleCancel() {
		dispatch('cancel');
		dispatch('close');
	}

	function getIcon() {
		switch (type) {
			case 'warning':
			case 'error':
				return '⚠️';
			case 'success':
				return '✅';
			default:
				return 'ℹ️';
		}
	}

	function getTypeClasses() {
		switch (type) {
			case 'warning':
				return 'border-orange-500 bg-orange-50 dark:bg-orange-950';
			case 'error':
				return 'border-red-500 bg-red-50 dark:bg-red-950';
			case 'success':
				return 'border-green-500 bg-green-50 dark:bg-green-950';
			default:
				return 'border-blue-500 bg-blue-50 dark:bg-blue-950';
		}
	}
</script>

<div class="space-y-4">
	<header class="flex items-center space-x-3">
		<span class="text-2xl">{getIcon()}</span>
		<h3 class="text-lg font-semibold">{title}</h3>
	</header>

	<article class="text-sm text-surface-600-400-token">
		{message}
	</article>

	<footer class="flex justify-end space-x-2">
		<button
			class="btn btn-ghost"
			on:click={handleCancel}
		>
			{cancelText}
		</button>
		<button
			class="btn btn-primary"
			on:click={handleConfirm}
		>
			{confirmText}
		</button>
	</footer>
</div>