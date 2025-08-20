<script lang="ts">
	import { createEventDispatcher } from 'svelte';

	const dispatch = createEventDispatcher();

	export let title = 'Alert';
	export let message = '';
	export let type: 'info' | 'warning' | 'error' | 'success' = 'info';

	function handleClose() {
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

	<footer class="flex justify-end">
		<button
			class="btn btn-primary"
			on:click={handleClose}
		>
			OK
		</button>
	</footer>
</div>