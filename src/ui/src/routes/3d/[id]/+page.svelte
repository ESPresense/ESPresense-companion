<script lang="ts">
	// Page to display the 3D map for a SINGLE device and its history
	import { page } from '$app/state'; // To get route params
	import { devices, nodes, config, showAllFloors } from '#lib/stores.js';
	import FloorTabs from '#lib/FloorTabs.svelte';
	import Map3D from '#lib/Map3D.svelte';
	import Map3DControls from '#lib/Map3DControls.svelte';
	import SlideToggle from '#lib/SlideToggle.svelte';
	import type { DeviceHistory } from '#lib/types.js';
	import { apiFetch } from '#lib/api.js';

	// --- Route Param ---
	let deviceId = $derived(page.params.id ?? null); // Get device ID from URL

	// Find the specific device based on the route ID
	let currentDevice = $derived($devices.find((d) => d.id === deviceId) || null);

	// --- State for this page ---
	let floorId: string | null = $state(null);
	let showNodes = $state(true);
	let autoRotate = $state(false);
	let showHistory = $state(false);
	let historyDurationMinutes = $state(60);
	let deviceHistoryData: DeviceHistory[] = $state([]);
	let map: ReturnType<typeof Map3D> | undefined = $state.raw();

	const historyOptions = [
		{ minutes: 15, label: '15 min' },
		{ minutes: 60, label: '1 hour' },
		{ minutes: 360, label: '6 hours' },
		{ minutes: 1440, label: '24 hours' }
	];

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

	// --- Data Fetching ---
	async function fetchDeviceHistory() {
		if (!deviceId || historyDurationMinutes <= 0) {
			deviceHistoryData = []; // Clear data if no ID or invalid duration
			return;
		}

		const endTime = new Date();
		const startTime = new Date(endTime.getTime() - historyDurationMinutes * 60 * 1000);

		try {
			const data = await apiFetch<{ history: DeviceHistory[] }>(`/api/history/${encodeURIComponent(deviceId)}/range?start=${startTime.toISOString()}&end=${endTime.toISOString()}`);
			deviceHistoryData = data.history || [];
		} catch (error) {
			console.error('Failed to fetch device history:', error);
			deviceHistoryData = []; // Clear data on error
		}
	}

	// Refetch history when relevant parameters change. One effect, not one per input:
	// fetchDeviceHistory() reads both deviceId and historyDurationMinutes synchronously,
	// so both are tracked here, and it already bails out on a missing id or duration.
	$effect(() => {
		if (showHistory) fetchDeviceHistory();
	});
</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map ({deviceId || 'Device'})</title>
</svelte:head>

<div class="w-full h-full relative">
	{#if $config && currentDevice && $nodes && floorId}
		<FloorTabs bind:floorId />
		<Map3DControls bind:showNodes bind:autoRotate hideDevicesToggle onview={(preset) => map?.frameView(preset)}>
			<SlideToggle name="show-history" bind:checked={showHistory} size="sm">
				<span>History</span>
			</SlideToggle>
			{#if showHistory}
				<label class="flex items-center justify-between gap-2">
					<span>Last</span>
					<select class="select w-28 py-0.5" bind:value={historyDurationMinutes} aria-label="History duration">
						{#each historyOptions as option (option.minutes)}
							<option value={option.minutes}>{option.label}</option>
						{/each}
					</select>
				</label>
			{/if}
		</Map3DControls>
		<Map3D bind:this={map} devicesToShow={[currentDevice]} nodesToShow={$nodes} config={$config} {floorId} showAllFloors={$showAllFloors} historyData={showHistory ? deviceHistoryData : []} showHistoryPath={showHistory} {showNodes} bind:autoRotate />
	{:else if !currentDevice && $devices}
		<div class="absolute inset-0 flex items-center justify-center text-white">
			Device with ID '{deviceId}' not found.
		</div>
	{:else}
		<div class="absolute inset-0 flex items-center justify-center text-white">Loading map and device data...</div>
	{/if}
</div>

<style>
	div {
		background-color: rgb(30, 41, 59);
	}
</style>
