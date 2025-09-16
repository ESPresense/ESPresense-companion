<script lang="ts">
	import { devices, showAllFloors } from '$lib/stores';
	import { zoomIdentity } from 'd3-zoom';

	import DeviceMarker from './DeviceMarker.svelte';
	import type { Device } from './types';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let exclusive: boolean = false;
	export let onhovered: ((device: Device | null) => void) | undefined = undefined;
	export let onselected: ((device: Device) => void) | undefined = undefined;

	function visible(d: Device) {
		if (exclusive) return d.id === deviceId;
		if (d.confidence <= 1 || !d.location) return false;
		if ($showAllFloors) return true;
		return d.floor?.id === floorId;
	}
</script>

<g transform={transform.toString()}>
	{#if $devices}
		{#each $devices as d (d.id)}
			<DeviceMarker {d} visible={visible(d)} {onhovered} {onselected} />
		{/each}
	{/if}
</g>
