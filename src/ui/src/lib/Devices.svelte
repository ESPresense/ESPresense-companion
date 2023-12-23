<script lang="ts">
	import { devices, showAll } from '$lib/stores';
	import { zoomIdentity } from 'd3-zoom';

	import DeviceMarker from './DeviceMarker.svelte';
	import type { Device } from './types';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let exclusive: boolean = false;

	function visible(d: Device) {
		if (exclusive) return d.id === deviceId;
		if (d.confidence <= 1 || !d.location) return false;
		if ($showAll) return true;
		return d.floor?.id === floorId;
	}
</script>

<g transform={transform.toString()}>
	{#if $devices}
		{#each $devices as d (d.id)}
			<DeviceMarker {d} visible={visible(d)} on:hovered on:selected />
		{/each}
	{/if}
</g>
