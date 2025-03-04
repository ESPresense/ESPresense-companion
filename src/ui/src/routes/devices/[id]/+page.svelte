<script lang="ts">
	import { base } from '$app/paths';
	import { devices } from '$lib/stores';
	import { readable } from 'svelte/store';
	import type { DeviceSetting } from '$lib/types';
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';

	import Map from '$lib/Map.svelte';
	import DeviceDetailTabs from '$lib/DeviceDetailTabs.svelte';
	import DeviceSettings from '$lib/DeviceSettings.svelte';

	export let tab = 'map';
	export let data: { settings?: DeviceSetting } = {};
	$: device = $devices?.find((d) => d.id === data.settings?.id);

	export const deviceDetails = readable([], (set) => {
		async function fetchAndSet() {
			try {
				const response = await fetch(`${base}/api/device/${data.settings?.id}`);
				const result = await response.json();
				set(result.details);
			} catch (ex) {
				console.error(ex);
			}
		}

		fetchAndSet();
		const interval = setInterval(() => {
			fetchAndSet();
		}, 1000);

		return function stop() {
			clearInterval(interval);
		};
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
		{/if}
		{#if tab === 'settings'}
			<DeviceSettings settings={data.settings} />
		{/if}
	</div>
	<div class="w-64 z-1 max-h-screen overflow-auto">
		<Accordion>
			<AccordionItem spacing="space-y-4" open>
				<svelte:fragment slot="summary">
					<h3 class="h3">Details</h3>
				</svelte:fragment>
				<svelte:fragment slot="content">
					{#if $deviceDetails}
						{#each $deviceDetails as d}
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
