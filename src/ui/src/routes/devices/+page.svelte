<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import { detail } from '$lib/urls';
	import { SlideToggle } from '@skeletonlabs/skeleton';
	import { showUntracked } from '$lib/stores';
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="container mx-auto p-2">
	<div class="flex justify-between items-center my-2 px-2">
		<h1 class="text-3xl font-bold">Devices</h1>
		<div class="flex items-center space-x-4">
			<SlideToggle name="show-untracked" bind:checked={$showUntracked}>Show Untracked</SlideToggle>
		</div>
	</div>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
