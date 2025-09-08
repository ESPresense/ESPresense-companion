<script lang="ts">
	import { base } from '$app/paths';
	import { devices } from '$lib/stores';
	import { readable, derived } from 'svelte/store';
	import { page } from '$app/stores';
	import type { DeviceSetting } from '$lib/types';
	import { Accordion, AccordionItem } from '$lib/skeleton-v3';

	import Map from '$lib/Map.svelte';
	import DeviceDetailTabs from '$lib/DeviceDetailTabs.svelte';
	import DeviceCalibration from '$lib/DeviceCalibration.svelte';
	import DeviceSettings from '$lib/DeviceSettings.svelte';

	// Define type for the details array items
	type DeviceDetailItem = { key: string; value: string };

	export let data: { settings?: DeviceSetting } = {};

	// Get tab from URL query parameter or default to 'map'
	$: tab = $page.url.searchParams.get('tab') || 'map';
	$: device = $devices?.find((d) => d.id === data.settings?.id);

	export const deviceDetails = readable<DeviceDetailItem[]>([], (set) => {
		const deviceId = data.settings?.id;
		if (!deviceId) return () => {};

		async function fetchAndSet() {
			try {
				const response = await fetch(`${base}/api/device/${deviceId}`);
				if (!response.ok) throw new Error(`HTTP error ${response.status}`);
				const result = await response.json();
				set(result.details || []);
			} catch (ex) {
				console.error(`Error fetching device details: ${ex instanceof Error ? ex.message : String(ex)}`);
				set([]);
			}
		}

		fetchAndSet();
		const interval = setInterval(fetchAndSet, 1000);

		return () => clearInterval(interval);
	});
</script>

<svelte:head>
	<title>ESPresense Companion: Device Detail</title>
</svelte:head>

<DeviceDetailTabs deviceId={data.settings?.id} floorId={device?.floor?.id} bind:tab />

<div class="flex h-full">
	<div class="flex-grow h-full overflow-clip">
		{#if tab === 'map'}
			<Map deviceId={data.settings?.id} floorId={device?.floor?.id} exclusive={true} />
		{:else if tab === 'calibration'}
			{#if data.settings?.id}
				<DeviceCalibration deviceSettings={data.settings} />
			{:else}
				<p class="p-4">Device ID not found.</p>
			{/if}
		{/if}
	</div>
	<div class="w-64 z-1 max-h-screen overflow-auto">
		<Accordion>
			<AccordionItem spacing="space-y-4" open>
				<svelte:fragment slot="summary">
					<h3 class="h3">Details</h3>
				</svelte:fragment>
				<svelte:fragment slot="content">
					{#if $deviceDetails && $deviceDetails.length > 0}
						{#each $deviceDetails as d}
							<label class="flex flex-col gap-1">
								<span>{d.key}</span>
								<input class="input" type="text" disabled value={d.value} />
							</label>
						{:else}
							<p class="text-sm italic">No details available</p>
						{/each}
					{:else}
						<p class="text-sm italic">Loading details...</p>
					{/if}
				</svelte:fragment>
			</AccordionItem>
		</Accordion>
	</div>
</div>
