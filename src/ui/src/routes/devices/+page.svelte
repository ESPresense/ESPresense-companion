<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import SlideToggle from '$lib/SlideToggle.svelte';
	import { gotoDetail } from '$lib/urls';
	import { showAll, wsManager } from '$lib/stores';
	import { onMount } from 'svelte';
	import { get } from 'svelte/store';

	let locatingStatus = 'Starting';

	onMount(() => {
		(async () => {
			try {
				const res = await fetch('/api/state/locator');
				if (res.ok) {
					const health = await res.json();
					locatingStatus = health.status;
				}
			} catch (e) {
				console.error('Failed to fetch health status', e);
			}
		})();

		const locatorStatusCallback = (data: any) => {
			if (data?.status) {
				locatingStatus = data.status;
			}
		};
		wsManager.subscribeToEvent('locatorStateChanged', locatorStatusCallback);

		return () => {
			wsManager.unsubscribeFromEvent('locatorStateChanged', locatorStatusCallback);
		};
	});
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-4 space-y-6">
		<header class="flex items-center justify-between">
			<h1 class="text-2xl font-bold text-surface-900-100">Devices</h1>
			<div class="flex items-center space-x-4">
				<SlideToggle name="show-all" bind:checked={$showAll}>Show All</SlideToggle>
			</div>
		</header>

		<div>
			<span class="text-sm font-semibold text-surface-600-400">Active Locators:</span>
			<span class="text-surface-900-100 ml-2">{locatingStatus}</span>
		</div>

		<section>
			<DevicesTable onselected={(device) => gotoDetail(device)} />
		</section>
	</div>
</div>
