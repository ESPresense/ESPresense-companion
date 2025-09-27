<script lang="ts">
	import NodeCalibrationMatrix from '$lib/NodeCalibrationMatrix.svelte';
	import DeviceCalibrationManager from '$lib/DeviceCalibrationManager.svelte';
	import CalibrationTabs from '$lib/CalibrationTabs.svelte';

	let calibrationType = 'node';
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
	{/if}
</div>
