<script lang="ts">
	// Page to display the 3D map for ALL devices
	import { onMount, onDestroy } from 'svelte';
	import { GUI } from 'three/examples/jsm/libs/lil-gui.module.min.js';
	import { devices, nodes, config } from '$lib/stores';
	import Map3D from '$lib/Map3D.svelte'; // Import the new component

	// --- State for GUI Controls ---
	let guiInstance: GUI | null = null;
	let showNodes = true;
	let showDevices = true;
	let zRotationSpeed = 0.002;

	const effectController = {
		zRotationSpeed: 0.002,
		showNodes: true,
		showDevices: true
	};

	// --- Reactive synchronization ---
	$: if (effectController && effectController.zRotationSpeed !== zRotationSpeed) {
		effectController.zRotationSpeed = zRotationSpeed;
	}

	$: if (effectController && effectController.showNodes !== showNodes) {
		effectController.showNodes = showNodes;
	}

	$: if (effectController && effectController.showDevices !== showDevices) {
		effectController.showDevices = showDevices;
	}

	// --- Lifecycle ---
	onMount(() => {
		doGuiSetup();

		return () => {
			guiInstance?.destroy();
		};
	});

	// --- GUI Setup ---
	function doGuiSetup() {
		guiInstance?.destroy(); // Ensure previous instance is removed
		guiInstance = new GUI({ title: 'Visualization Settings' });

		guiInstance
			.add(effectController, 'zRotationSpeed', 0, Math.PI / 2, 0.01)
			.name('Rotation Speed')
			.onChange((value: number) => {
				zRotationSpeed = value; // Update the reactive variable passed as prop
			});

		guiInstance
			.add(effectController, 'showNodes')
			.name('Show Nodes')
			.onChange((value: boolean) => {
				showNodes = value; // Update the reactive variable passed as prop
			});

		guiInstance
			.add(effectController, 'showDevices')
			.name('Show Devices')
			.onChange((value: boolean) => {
				showDevices = value; // Update the reactive variable passed as prop
			});

		guiInstance.close(); // Start closed
	}
</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map (All Devices)</title>
</svelte:head>

<div class="w-full h-full relative bg-surface-50-950">
	{#if $config && $devices && $nodes}
		<Map3D devicesToShow={$devices} nodesToShow={$nodes} config={$config} bind:showNodes bind:showDevices bind:zRotationSpeed showHistoryPath={false} historyData={[]} />
	{:else}
		<div class="absolute inset-0 flex items-center justify-center text-white">Loading map data...</div>
	{/if}
</div>