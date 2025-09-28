<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import SlideToggle from '$lib/SlideToggle.svelte';
	import { gotoDetail } from '$lib/urls';
	import { showAll } from '$lib/stores';
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

		<section>
			<DevicesTable onselected={(device) => gotoDetail(device)} />
		</section>
	</div>
</div>
