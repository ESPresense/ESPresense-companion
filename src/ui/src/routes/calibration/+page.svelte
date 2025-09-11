<script lang="ts">
	import NodeCalibrationMatrix from '$lib/NodeCalibrationMatrix.svelte';
	import DeviceCalibrationManager from '$lib/DeviceCalibrationManager.svelte';
	import CalibrationTabs from '$lib/CalibrationTabs.svelte';

	let calibrationType = 'node';
</script>

<svelte:head>
	<title>ESPresense Companion: Calibration</title>
</svelte:head>

<div class="flex flex-col h-full">
	<div class="flex-shrink-0">
		<CalibrationTabs bind:calibrationType />
	</div>
	<div class="flex-1 min-h-0">
		{#if calibrationType === 'node'}
			<div class="h-full overflow-y-auto">
				<div class="w-full px-4 py-2">
					<h1 class="text-2xl font-bold mb-4">Node Calibration</h1>
					<NodeCalibrationMatrix />
				</div>
			</div>
		{:else if calibrationType === 'device'}
			<DeviceCalibrationManager />
		{/if}
	</div>
</div>
