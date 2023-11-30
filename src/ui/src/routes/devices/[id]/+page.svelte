<script lang="ts">
	import { base } from '$app/paths';
	import { devices } from '$lib/stores';
	import type { DeviceSetting } from '$lib/types';
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';

	import Map from '$lib/Map.svelte';
	import DetailTabs from '$lib/DetailTabs.svelte';
	import DeviceDetails from '$lib/DeviceDetails.svelte';

	export let tab = 'map';
	export let data: { settings?: DeviceSetting; details?: [] } = {};
	$: device = $devices.find((d) => d.id === data.settings?.id);

	var outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		fetch(`${base}/api/device/${data.settings?.id}`)
			.then((d) => d.json())
			.then((r) => {
				outstanding = false;
				data.details = r.details;
			})
			.catch((ex) => {
				outstanding = false;
				console.log(ex);
			});
	}, 1000);
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<DetailTabs deviceId={data.settings?.id} floorId={device?.floor?.id} bind:tab />

<div class="flex h-full">
	<div class="flex-grow h-full overflow-clip">
		{#if tab === 'map'}
			<Map deviceId={data.settings?.id} floorId={device?.floor?.id} />
		{/if}
		{#if tab === 'settings'}
			<DeviceDetails settings={data.settings} details={data.details} />
		{/if}
	</div>
	<div class="w-64 z-1 max-h-screen overflow-auto">
		<Accordion>
			<AccordionItem spacing="space-y-4" open>
				<svelte:fragment slot="summary">
					<h3 class="h3">Details</h3>
				</svelte:fragment>
				<svelte:fragment slot="content">
					{#if data?.details}
						{#each data?.details as d}
							<label>
								<span>{d.key}</span>
								<input class="input" type="text" disabled bind:value={d.value} />
							</label>
						{/each}
					{/if}
				</svelte:fragment>
			</AccordionItem>
		</Accordion>
	</div>
</div>
