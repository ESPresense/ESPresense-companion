<script lang="ts">
	// Page to display the 3D map for a SINGLE device and its history
	import { onMount, onDestroy } from 'svelte';
	import { page } from '$app/stores'; // To get route params
	import { GUI } from 'three/examples/jsm/libs/lil-gui.module.min.js';
	import { devices, nodes, config } from '$lib/stores';
	import Map3D from '$lib/Map3D.svelte';
	import type { Device, Node, Config, DeviceHistory } from '$lib/types';
	import { derived } from 'svelte/store';

	// --- Route Param ---
	let deviceId: string | null = null;
	$: deviceId = $page.params.id; // Get device ID from URL

	// --- Data Stores & Derived State ---
	// Find the specific device based on the route ID
	const currentDevice = derived([devices, page], ([$devices, $page]) => {
		return $devices.find(d => d.id === $page.params.id) || null;
	});

	// --- State for this page ---
	let guiInstance: GUI | null = null;
	let historyDurationMinutes: number = 60;
	let deviceHistoryData: DeviceHistory[] = [];
	let displayMode: 'current' | 'history' = 'current'; // Control what's shown
	let showNodes = true; // Separate control for nodes on this page
	let zRotationSpeed = 0.002; // Separate control for rotation

	// Reactive controller for GUI
	const effectController = {
		displayMode: 'current',
		historyDurationMinutes: 60,
		showNodes: true,
		zRotationSpeed: 0.002
	};

	// --- Lifecycle ---
	onMount(() => {
		doGuiSetup();
		// Fetch history initially if mode is 'history' (though default is 'current')
		if (displayMode === 'history') {
			fetchDeviceHistory();
		}
		return () => {
			guiInstance?.destroy();
		};
	});

	// Refetch history when relevant parameters change
	$: if (deviceId && displayMode === 'history') fetchDeviceHistory();
	$: if (historyDurationMinutes && displayMode === 'history') fetchDeviceHistory();

	// --- Data Fetching ---
	async function fetchDeviceHistory() {
		if (!deviceId || historyDurationMinutes <= 0) {
			deviceHistoryData = []; // Clear data if no ID or invalid duration
			return;
		}

		const endTime = new Date();
		const startTime = new Date(endTime.getTime() - historyDurationMinutes * 60 * 1000);

		try {
			console.log(`Fetching history for ${deviceId} from ${startTime.toISOString()} to ${endTime.toISOString()}`);
			const response = await fetch(
				`/api/history/${deviceId}/range?start=${startTime.toISOString()}&end=${endTime.toISOString()}`
			);
			if (!response.ok) throw new Error(`HTTP error! status: ${response.status}`);

			const data: { history: DeviceHistory[] } = await response.json();
			deviceHistoryData = data.history || [];
			console.log(`Fetched ${deviceHistoryData.length} history points.`);
		} catch (error) {
			console.error('Failed to fetch device history:', error);
			deviceHistoryData = []; // Clear data on error
		}
	}

	// --- GUI Setup ---
	function doGuiSetup() {
		guiInstance?.destroy();
		guiInstance = new GUI({ title: 'Device View Settings' });

		// Display Mode (Current vs. History)
		guiInstance.add(effectController, 'displayMode', ['current', 'history'])
			.name('Display')
			.onChange((value: string) => {
				// Assert the type before assigning, as lil-gui provides string
				if (value === 'current' || value === 'history') {
					displayMode = value;
				}
				// No need to fetch here, reactive statement handles it
			});

		// History Duration (only relevant when displayMode is 'history')
		const historyDurationControl = guiInstance.add(effectController, 'historyDurationMinutes', 5, 1440, 5)
			.name('History (min)')
			.onChange((value: number) => {
				historyDurationMinutes = value;
				// No need to fetch here, reactive statement handles it
			});

		// Show/Hide duration control based on mode
		$: historyDurationControl.domElement.style.display = (displayMode === 'history') ? '' : 'none';


		// General Visualization
		const vizFolder = guiInstance.addFolder('Visualization');
		vizFolder.add(effectController, 'zRotationSpeed', 0, Math.PI / 2, 0.01)
			.name('Rotation Speed')
			.onChange((value: number) => {
				zRotationSpeed = value;
			});

		vizFolder.add(effectController, 'showNodes')
			.name('Show Nodes')
			.onChange((value: boolean) => {
				showNodes = value;
			});

		guiInstance.close();
	}

</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map ({deviceId || 'Device'})</title>
</svelte:head>

<div class="w-full h-full relative">
	{#if $config && $currentDevice && $nodes}
		<Map3D
			devicesToShow={displayMode === 'current' ? [$currentDevice] : []}
			nodesToShow={$nodes}
			config={$config}
			historyData={displayMode === 'history' ? deviceHistoryData : []}
			showDevices={displayMode === 'current'}
			showHistoryPath={displayMode === 'history'}
			bind:showNodes={showNodes}
			bind:zRotationSpeed={zRotationSpeed}
		/>
	{:else if !$currentDevice && $devices}
		<div class="absolute inset-0 flex items-center justify-center text-white">
			Device with ID '{deviceId}' not found.
		</div>
	{:else}
		<div class="absolute inset-0 flex items-center justify-center text-white">
			Loading map and device data...
		</div>
	{/if}
</div>

<style>
	div {
		background-color: rgb(30, 41, 59);
	}
</style>