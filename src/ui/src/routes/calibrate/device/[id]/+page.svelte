<script lang="ts">
	import { page } from '$app/stores';
	import DeviceCalibration from '$lib/DeviceCalibration.svelte';
	import { onMount } from 'svelte';
	import { base } from '$app/paths';
	import * as urls from '$lib/urls';

	let deviceSettings: any = null;
	let loading = true;
	let error: string | null = null;

	$: deviceId = $page.params.id;

	async function fetchDeviceSettings() {
		if (!deviceId) return;
		
		try {
			loading = true;
			error = null;
			const response = await fetch(`${base}/api/device/${deviceId}`);
			
			if (response.ok) {
				deviceSettings = await response.json();
			} else {
				const errorData = await response.text();
				error = `Error fetching device settings: ${errorData || response.statusText}`;
			}
		} catch (e) {
			console.error(`Error fetching settings for device ${deviceId}:`, e);
			error = 'Error fetching device settings.';
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		fetchDeviceSettings();
	});

	$: if (deviceId) {
		fetchDeviceSettings();
	}

	function navigateToDevices() {
		urls.gotoDevices();
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Calibrate Device {deviceId}</title>
</svelte:head>

<div class="h-full overflow-auto">
	{#if loading}
		<div class="flex items-center justify-center h-64">
			<div class="text-center">
				<div class="animate-spin rounded-full h-12 w-12 border-b-2 border-primary-500 mx-auto mb-4"></div>
				<p>Loading device settings...</p>
			</div>
		</div>
	{:else if error}
		<div class="card p-4 mx-4 my-4 preset-filled-error-500">
			<h2 class="text-xl font-bold mb-2">Error</h2>
			<p>{error}</p>
			<button onclick={navigateToDevices} class="btn preset-filled-primary-500 mt-4">← Back to Devices</button>
		</div>
	{:else if deviceSettings}
		<DeviceCalibration {deviceSettings} />
	{:else}
		<div class="card p-4 mx-4 my-4 preset-tonal">
			<h2 class="text-xl font-bold mb-2">Device Not Found</h2>
			<p>No device found with ID: {deviceId}</p>
			<button onclick={navigateToDevices} class="btn preset-filled-primary-500 mt-4">← Back to Devices</button>
		</div>
	{/if}
</div>
