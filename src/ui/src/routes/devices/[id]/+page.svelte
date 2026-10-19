<script lang="ts">
	import { apiFetch } from '$lib/api';
	import { devices, config } from '$lib/stores';
	import FingerprintCollection from '$lib/FingerprintCollection.svelte';
	import { readable } from 'svelte/store';
	import type { DeviceSetting } from '$lib/types';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';

	import Map from '$lib/Map.svelte';
	import DeviceBreadcrumb from '$lib/DeviceBreadcrumb.svelte';

	// Define type for the details array items
	type DeviceDetailItem = { key: string; value: string };

	let { data = {} }: { data: { settings?: DeviceSetting } } = $props();

	let device = $derived($devices?.find((d) => d.id === data.settings?.id));

	let accordionValue = $state(['details']);
	let updatedSettings = $state<DeviceSetting | null>(null);
	let effectiveSettings = $derived(updatedSettings?.id === data.settings?.id ? updatedSettings : data.settings);
	let teaching = $state(false);
	let teachingFloor = $state<string | null>(null);
	let spot = $state<{ x: number; y: number } | null>(null);
	let height = $state(1);
	let currentRefRssi = $state<number | null>(null);
	let teachingBounds = $derived($config?.floors.find((f) => f.id === teachingFloor)?.bounds);

	function resetSpot() {
		const bounds = $config?.floors.find((f) => f.id === teachingFloor)?.bounds;
		spot = bounds ? { x: (bounds[0][0] + bounds[1][0]) / 2, y: (bounds[0][1] + bounds[1][1]) / 2 } : null;
	}

	function teachHere() {
		teachingFloor = device?.floor?.id ?? $config?.floors[0]?.id ?? null;
		resetSpot();
		if (device?.location) spot = { x: device.location.x, y: device.location.y };
		currentRefRssi = effectiveSettings?.['rssi@1m'] ?? null;
		teaching = true;
	}

	const deviceDetails = readable<DeviceDetailItem[]>([], (set) => {
		const deviceId = data.settings?.id;
		if (!deviceId) return () => {};

		async function fetchAndSet() {
			try {
				const result = await apiFetch<{ details?: DeviceDetailItem[] }>(`/api/device/${deviceId}`);
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
	<div class="flex flex-col flex-grow min-h-0 overflow-y-auto">
		<!-- Breadcrumb Navigation -->
		<DeviceBreadcrumb deviceName={device?.name || device?.id || 'Unknown Device'} currentView="map" />

		<div class="p-3 flex flex-wrap items-center gap-3">
			{#if teaching}
				<label
					>Floor
					<select class="select" bind:value={teachingFloor} onchange={resetSpot}>
						{#each $config?.floors ?? [] as floor}<option value={floor.id}>{floor.name}</option>{/each}
					</select>
				</label>
				<label>Height from floor (m)<input class="input" type="number" min="0" max="5" step="0.1" bind:value={height} /></label>
				<button class="btn preset-filled-surface-500" onclick={() => (teaching = false)}>Close teaching</button>
			{:else}
				<button class="btn preset-filled-primary-500" onclick={teachHere} disabled={!data.settings?.id}>I'm here</button>
			{/if}
		</div>
		<div class="grid flex-1 min-h-[300px]">
			{#if teaching}
				<Map deviceId={data.settings?.id} floorId={teachingFloor} exclusive={true} calibrate={true} bind:calibrationSpot={spot} />
			{:else}
				<Map deviceId={data.settings?.id} floorId={device?.floor?.id} exclusive={true} />
			{/if}
		</div>
		{#if teaching}
			<div class="p-3">
				<FingerprintCollection
					deviceId={data.settings?.id ?? null}
					floorId={teachingFloor}
					position={spot ? { ...spot, z: (teachingBounds?.[0]?.[2] ?? 0) + height } : null}
					{currentRefRssi}
					oncalibrated={(settings) => {
						currentRefRssi = settings['rssi@1m'];
						updatedSettings = settings;
					}}
				/>
			</div>
		{/if}
	</div>
	<div class="w-64 flex-shrink-0 bg-surface-100-800 border-l border-surface-300-700 overflow-auto">
		<Accordion value={accordionValue} onValueChange={({ value }) => (accordionValue = value)}>
			<Accordion.Item value="details">
				<Accordion.ItemTrigger>
					<h3 class="text-lg font-semibold">Details</h3>
				</Accordion.ItemTrigger>
				<Accordion.ItemContent>
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
				</Accordion.ItemContent>
			</Accordion.Item>
		</Accordion>
	</div>
</div>
