<script lang="ts">
	import { devices } from '$lib/stores';
	import type { Device, DeviceSetting } from '$lib/types';

	import Map from '$lib/Map.svelte';
	import DetailTabs from '$lib/DetailTabs.svelte';
	import DeviceDetails from '$lib/DeviceDetails.svelte';

	export let tab = 'map';
	export let data: { settings?: DeviceSetting; details?: Device } = {};
	$: device = $devices.find((d) => d.id === data.settings?.id);
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<DetailTabs deviceId={data.settings?.id} floorId={device?.floor?.id} bind:tab />
{#if tab === 'map'}
	<Map deviceId={data.settings?.id} floorId={device?.floor?.id} />
{/if}
{#if tab === 'details'}
	<DeviceDetails settings={data.settings} details={data.details} />
{/if}
