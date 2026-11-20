<script lang="ts">
	// Page to display the 3D map for a SINGLE device and its history
	import { page } from '$app/state'; // To get route params
	import { devices, nodes, config, showAllFloors } from '#lib/stores.js';
	import FloorTabs from '#lib/FloorTabs.svelte';
	import Map3D from '#lib/Map3D.svelte';
	import Map3DControls from '#lib/Map3DControls.svelte';
	import DeviceHistoryPanel from '#lib/DeviceHistoryPanel.svelte';
	import type { HistoryView } from '#lib/history.js';

	// --- Route Param ---
	let deviceId = $derived(page.params.id ?? null); // Get device ID from URL

	// Find the specific device based on the route ID
	let currentDevice = $derived($devices.find((d) => d.id === deviceId) || null);

	// --- State for this page ---
	let floorId: string | null = $state(null);
	let showNodes = $state(true);
	let autoRotate = $state(false);
	let map: ReturnType<typeof Map3D> | undefined = $state.raw();
	// The same scrubber as the device page: its trail and the moment shown
	let history = $state.raw<HistoryView | null>(null);

	// Open on the device's own floor. Set once, so the floor tabs stay in charge afterwards.
	let floorPinned = false;
	$effect(() => {
		if (floorPinned || !currentDevice) return;
		// Wait for a real floor: pinning on null would leave the page on "Loading" for good,
		// since the floor tabs that could pick one only render once there is a floor.
		const initialFloorId = currentDevice.floor?.id ?? $config?.floors?.[0]?.id ?? null;
		if (!initialFloorId) return;
		floorPinned = true;
		floorId = initialFloorId;
	});

	// While scrubbing, follow the device from floor to floor as the 2D device page does. Only when the floor
	// it was on changes, so the tabs still pick a floor in between.
	let followedFloorId: string | null = null;
	$effect(() => {
		const id = history && !history.live && history.current ? (history.floor?.id ?? null) : null;
		if (id && id !== followedFloorId) floorId = id;
		followedFloorId = id;
	});
</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map ({deviceId || 'Device'})</title>
</svelte:head>

<div class="w-full h-full flex flex-col">
	{#if $config && currentDevice && $nodes && floorId}
		<div class="relative min-h-0 flex-1">
			<FloorTabs bind:floorId />
			<Map3DControls bind:showNodes bind:autoRotate hideDevicesToggle onview={(preset) => map?.frameView(preset)} />
			<Map3D bind:this={map} devicesToShow={[currentDevice]} nodesToShow={$nodes} config={$config} {floorId} showAllFloors={$showAllFloors} {history} {showNodes} bind:autoRotate />
		</div>
		<DeviceHistoryPanel deviceId={currentDevice.id} onview={(view) => (history = view)} />
	{:else if !currentDevice && $devices}
		<div class="flex flex-1 items-center justify-center text-white">
			Device with ID '{deviceId}' not found.
		</div>
	{:else}
		<div class="flex flex-1 items-center justify-center text-white">Loading map and device data...</div>
	{/if}
</div>

<style>
	div {
		background-color: rgb(30, 41, 59);
	}
</style>
