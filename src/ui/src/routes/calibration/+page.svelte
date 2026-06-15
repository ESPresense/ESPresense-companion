<script lang="ts">
	import NodeCalibrationMatrix from '$lib/NodeCalibrationMatrix.svelte';
	import DeviceCalibrationManager from '$lib/DeviceCalibrationManager.svelte';
	import CalibrationTabs from '$lib/CalibrationTabs.svelte';
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';

	let calibrationType = 'node';
	let floorId: string | null = null;
</script>

<svelte:head>
	<title>ESPresense Companion: Calibration</title>
</svelte:head>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-4 space-y-6">
		<header class="flex flex-wrap items-center justify-between gap-4">
			<h1 class="text-2xl font-bold text-surface-900-100">Calibration</h1>
			<CalibrationTabs bind:calibrationType />
		</header>
	</div>
	{#if calibrationType === 'node'}
		<section>
			<NodeCalibrationMatrix />
		</section>
	{:else if calibrationType === 'device'}
		<section>
			<DeviceCalibrationManager />
		</section>
	{:else if calibrationType === 'obstructions'}
		<section class="px-4 space-y-2">
			<p class="text-sm text-surface-600-300">
				Static RF-attenuation map reconstructed from node-to-node links (radio tomography). Hot cells are where the radio sees something solid — walls, appliances, a refrigerator. This is a sanity check: if the hot blobs line up with things you know are there, the model is learning real physics. Updates every ~30s; sparse-coverage areas read cooler than reality.
			</p>
			<FloorTabs bind:floorId />
			<div class="w-full" style="height: 70vh">
				<Map bind:floorId showTomography />
			</div>
		</section>
	{/if}
</div>
