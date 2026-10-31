<script lang="ts">
	import { apiFetch } from '$lib/api';
	import { devices } from '$lib/stores';
	import { readable } from 'svelte/store';
	import type { DeviceExplanation, DeviceSetting } from '$lib/types';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';

	import Map from '$lib/Map.svelte';
	import DeviceBreadcrumb from '$lib/DeviceBreadcrumb.svelte';
	import DeviceExplain from '$lib/DeviceExplain.svelte';

	let { data = {} }: { data: { settings?: DeviceSetting } } = $props();

	let device = $derived($devices?.find((d) => d.id === data.settings?.id));

	let accordionValue = $state(['explain']);

	const explanation = readable<DeviceExplanation | null>(null, (set) => {
		const deviceId = data.settings?.id;
		if (!deviceId) return () => {};

		async function fetchAndSet() {
			try {
				set(await apiFetch<DeviceExplanation>(`/api/device/${deviceId}/explain`));
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
	<div class="flex flex-col flex-grow min-h-0">
		<!-- Breadcrumb Navigation -->
		<DeviceBreadcrumb deviceName={device?.name || device?.id || 'Unknown Device'} currentView="map" />

		<div class="grid flex-1 min-h-0">
			<Map deviceId={data.settings?.id} floorId={device?.floor?.id} explanation={$explanation} exclusive={true} />
		</div>
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
