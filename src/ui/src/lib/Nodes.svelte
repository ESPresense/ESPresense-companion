<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import type { Device, Node, Floor } from '$lib/types';

	import NodeMarker from './NodeMarker.svelte';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let radarDevice: Device | undefined = undefined;
	export let radarNode: Node | undefined = undefined;

	let floor: Floor | undefined;
	$: selectedNodes = $nodes?.filter((n) => !floorId || n?.floors.includes(floorId));
	$: floor = $config?.floors?.find((f) => f.id == floorId);
</script>

<g transform={transform.toString()}>
	{#if nodes}
		{#each selectedNodes as n (n.id)}
			<NodeMarker {n} {radarDevice} {radarNode} {floor} on:hovered on:selected />
		{/each}
	{/if}
</g>
