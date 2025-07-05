<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import { detail } from '$lib/urls';
	import { showAll } from '$lib/stores';
	
	function toggleShowAll() {
		$showAll = !$showAll;
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="container mx-auto p-2">
	<div class="flex justify-between items-center my-2 px-2">
		<h1 class="text-3xl font-bold">Devices</h1>
		<div class="flex items-center space-x-4">
			<div class="flex items-center space-x-3">
				<button
					class="relative inline-flex h-6 w-11 items-center rounded-full transition-colors bg-slate-600"
					on:click={toggleShowAll}
					aria-label="Toggle show all devices"
				>
					<span
						class="inline-block h-4 w-4 transform rounded-full bg-white transition-transform {$showAll ? 'translate-x-6' : 'translate-x-1'}"
					></span>
				</button>
				<span class="text-sm font-medium">Show All</span>
			</div>
		</div>
	</div>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
