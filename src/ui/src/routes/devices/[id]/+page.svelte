<script lang="ts">
	import { base } from '$app/paths';
	import { devices } from '$lib/stores';
	import { readable, derived } from 'svelte/store';
	import { page } from '$app/stores';
	import type { DeviceSetting } from '$lib/types';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';

	import Map from '$lib/Map.svelte';
	import DeviceCalibration from '$lib/DeviceCalibration.svelte';
	import DeviceSettings from '$lib/DeviceSettings.svelte';
	import DeviceBreadcrumb from '$lib/DeviceBreadcrumb.svelte';

	// Define type for the details array items
	type DeviceDetailItem = { key: string; value: string };

	let { data = {} }: { data: { settings?: DeviceSetting } } = $props();

	// Get tab from URL query parameter or default to 'map'
	let currentTab = $state($page.url.searchParams.get('tab') || 'map');
	let device = $derived($devices?.find((d) => d.id === data.settings?.id));

	// Accordion state using $state for Skeleton v3
	let accordionValue = $state(['details']);

	const deviceDetails = readable<DeviceDetailItem[]>([], (set) => {
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

<div class="flex h-full min-h-0">
	<div class="flex flex-col grow min-h-0">
		<!-- Breadcrumb Navigation -->
		<DeviceBreadcrumb deviceName={device?.name || device?.id || 'Unknown Device'} currentView={currentTab === 'calibration' ? 'calibration' : 'map'} />

		{#if currentTab === 'map'}
			<div class="grid flex-1 min-h-0">
				<Map deviceId={data.settings?.id} floorId={device?.floor?.id} exclusive={true} />
			</div>
		{:else if currentTab === 'calibration'}
			{#if data.settings?.id}
				<div class="flex-1 min-h-0 overflow-auto">
					<DeviceCalibration deviceSettings={data.settings} />
				</div>
			{:else}
				<div class="flex-1 min-h-0">
					<p class="p-4">Device ID not found.</p>
				</div>
			{/if}
		{/if}
	</div>
	<div class="w-64 shrink-0 bg-surface-100-800 border-l border-surface-300-700 overflow-auto">
		<Accordion value={accordionValue} onValueChange={(e) => (accordionValue = e.value)}>
			<Accordion.Item value="details">
				{#snippet control()}
					<h3 class="text-lg font-semibold">Details</h3>
				{/snippet}
				{#snippet panel()}
					<div class="space-y-3 p-1">
						{#if $deviceDetails}
							{#if $deviceDetails.length > 0}
								{#each $deviceDetails as d}
									<label class="flex flex-col gap-1">
										<span class="text-sm font-medium">{d.key}</span>
										<input class="input rounded-full" type="text" disabled value={d.value} />
									</label>
								{/each}
							{:else}
								<p class="text-sm italic">No details available</p>
							{/if}
						{:else}
							<p class="text-sm italic">Loading details...</p>
						{/if}
					</div>
				{/snippet}
			</Accordion.Item>
		</Accordion>
	</div>
</div>
