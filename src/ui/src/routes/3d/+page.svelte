<script lang="ts">
	// Page to display the 3D map for ALL devices
	import { devices, nodes, config, showAllFloors } from '#lib/stores.js';
	import FloorTabs from '#lib/FloorTabs.svelte';
	import Map3D from '#lib/Map3D.svelte';
	import Map3DControls from '#lib/Map3DControls.svelte';

	// Local state, not a prop: SvelteKit route components only receive `data` and `form`.
	let floorId: string | null = $state(null);
	let showNodes = $state(true);
	let showDevices = $state(true);
	let autoRotate = $state(false);
	let map: ReturnType<typeof Map3D> | undefined = $state.raw();
</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map (All Devices)</title>
</svelte:head>

<div class="w-full h-full relative bg-surface-50-950">
	<FloorTabs bind:floorId />
	<Map3DControls bind:showNodes bind:showDevices bind:autoRotate onview={(preset) => map?.frameView(preset)} />
	{#if $config && $devices && $nodes}
		<Map3D bind:this={map} devicesToShow={$devices} nodesToShow={$nodes} config={$config} {floorId} showAllFloors={$showAllFloors} {showNodes} {showDevices} bind:autoRotate showHistoryPath={false} historyData={[]} />
	{:else}
		<div class="absolute inset-0 flex items-center justify-center text-white">Loading map data...</div>
	{/if}
</div>
