<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import type { Floor, Node } from '$lib/types';

	import NodeMarker from './NodeMarker.svelte';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let nodeId: string | null = null;
	export let onhovered: ((node: Node | null) => void) | undefined = undefined;
	export let onselected: ((node: Node) => void) | undefined = undefined;

	let floor: Floor | undefined;
	$: selectedNodes = $nodes?.filter((n) => !floorId || n?.floors.includes(floorId));
	$: floor = $config?.floors?.find((f) => f.id == floorId);
</script>

<g transform={transform.toString()}>
	{#if nodes}
		{#each selectedNodes as n (n.id)}
			<NodeMarker {n} {deviceId} {nodeId} {floor} {onhovered} {onselected} />
		{/each}
	{/if}
</g>
