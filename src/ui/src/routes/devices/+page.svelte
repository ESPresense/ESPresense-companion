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
	<h1 class="text-3xl font-bold my-2 px-2">Devices</h1>
	<div class="flex items-center mb-4">
		<SlideToggle name="show-untracked" bind:checked={$showUntracked}>Show Untracked</SlideToggle>
	</div>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
