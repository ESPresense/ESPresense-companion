<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';

	import Moveable from 'svelte-moveable';
	import { config } from '../lib/stores';

	import type { Node } from '../lib/types';

	import NodeMarker from './NodeMarker.svelte';

	export let transform = zoomIdentity;
	export let radarId: string = '';
	export let floorId: string = '';
	let target: any;

	let nodes: Node[] | undefined;
	$: nodes = $config?.nodes?.filter((n) => n.floors.includes(floorId));
</script>

<Moveable {target} draggable="true" edge={false} />

<g transform={transform.toString()}>
	{#if nodes}
		{#each nodes as n (n.id)}
			<NodeMarker {n} {radarId} />
		{/each}
	{/if}
</g>
