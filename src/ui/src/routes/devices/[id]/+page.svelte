<script lang="ts">
	import { apiFetch } from '#lib/api.js';
	import { devices, config, includedDevice } from '#lib/stores.js';
	import FingerprintCollection from '#lib/FingerprintCollection.svelte';
	import { readable } from 'svelte/store';
	import type { DeviceExplanation, DeviceSetting } from '#lib/types.js';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';

	import Map from '#lib/Map.svelte';
	import DeviceBreadcrumb from '#lib/DeviceBreadcrumb.svelte';
	import DeviceExplain from '#lib/DeviceExplain.svelte';

	let { data = {} }: { data: { settings?: DeviceSetting } } = $props();

	let device = $derived($devices?.find((d) => d.id === data.settings?.id));

	$effect(() => {
		includedDevice.set(data.settings?.id ?? null);
		return () => includedDevice.set(null);
	});

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

	let accordionValue = $state(['explain']);

	const explanation = readable<DeviceExplanation | null>(null, (set) => {
		const deviceId = data.settings?.id;
		if (!deviceId) return () => {};
		const url = `/api/device/${encodeURIComponent(deviceId)}/explain`;

		async function fetchAndSet() {
			try {
				set(await apiFetch<DeviceExplanation>(url));
			} catch (ex) {
				console.error(`Error fetching device explanation: ${ex instanceof Error ? ex.message : String(ex)}`);
				set(null);
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
				<Map deviceId={data.settings?.id} floorId={teachingFloor} exclusive={true} embedded={true} bind:calibrationSpot={spot} />
			{:else}
				<Map deviceId={data.settings?.id} floorId={device?.floor?.id} explanation={$explanation} exclusive={true} />
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
	<div class="w-80 flex-shrink-0 bg-surface-100-800 border-l border-surface-300-700 overflow-auto">
		<Accordion value={accordionValue} onValueChange={({ value }) => (accordionValue = value)}>
			<Accordion.Item value="explain">
				<Accordion.ItemTrigger>
					<h3 class="text-lg font-semibold">Explain this fix</h3>
				</Accordion.ItemTrigger>
				<Accordion.ItemContent>
					<DeviceExplain explanation={$explanation} />
				</Accordion.ItemContent>
			</Accordion.Item>
		</Accordion>
	</div>
</div>
