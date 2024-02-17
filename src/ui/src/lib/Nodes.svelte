<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import type { Floor } from '$lib/types';

	import NodeMarker from './NodeMarker.svelte';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let nodeId: string | null = null;

	let floor: Floor | undefined;
	$: selectedNodes = $nodes?.filter((n) => !floorId || n?.floors.includes(floorId));
	$: floor = $config?.floors?.find((f) => f.id == floorId);
</script>

<g transform={transform.toString()}>
	{#if nodes}
		{#each selectedNodes as n (n.id)}
			<NodeMarker {n} {deviceId} {nodeId} {floor} on:hovered on:selected />
		{/each}
	{/if}
</g>
