<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config } from '$lib/stores';
	import type { Node, Floor } from '$lib/types';

	import NodeMarker from './NodeMarker.svelte';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let radarId: string | null = null;

	let nodes: Node[] | undefined;
  let floor: Floor | undefined;
	$: nodes = floorId ? $config?.nodes?.filter((n) => n?.floors.includes(floorId)) : [];
  $: floor = $config?.floors?.find((f) => f.id == floorId);
</script>

<g transform={transform.toString()}>
	{#if nodes}
		{#each nodes as n (n.id)}
			<NodeMarker {n} {radarId} {floor} />
		{/each}
	{/if}
</g>
