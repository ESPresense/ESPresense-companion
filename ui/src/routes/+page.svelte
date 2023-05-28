<script lang="ts">
	import { base } from '$app/paths';
	import { goto } from '$app/navigation';
	import type { Device } from '$lib/types';
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';

	export let floorId: string | null = null;

	function detail (d:Device | null) {
		goto(`${base}/devices/${d?.id}`);
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full">
	<FloorTabs bind:floorId />
	<Map on:selected={d => detail(d.detail)} bind:floorId />
</div>
