<script lang="ts">
	import { devices, showAll } from '$lib/stores';
	import { zoomIdentity } from 'd3-zoom';

	import DeviceDot from './DeviceDot.svelte';
	import type { Device } from './types';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let deviceId: string | null = null;

	function visible(d: Device) {
		if ($showAll) return true;
		if (deviceId != null) return d.id === deviceId;
		return d.floor?.id === floorId;
	}
</script>

<g transform={transform.toString()}>
	{#if $devices}
		{#each $devices as d (d.id)}
			<DeviceDot {d} visible={visible(d)} on:hovered on:selected />
		{/each}
	{/if}
</g>
