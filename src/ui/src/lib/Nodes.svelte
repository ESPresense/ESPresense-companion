<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import type { Floor, Node } from '$lib/types';

	import NodeMarker from './NodeMarker.svelte';

	interface Props {
		transform?: any;
		floorId?: string | null;
		deviceId?: string | null;
		nodeId?: string | null;
		onhovered?: ((node: Node | null) => void) | undefined;
		onselected?: ((node: Node) => void) | undefined;
	}

	let { transform = zoomIdentity, floorId = null, deviceId = null, nodeId = null, onhovered = undefined, onselected = undefined }: Props = $props();

	let floor: Floor | undefined = $derived($config?.floors?.find((f) => f.id == floorId));
	let selectedNodes = $derived($nodes?.filter((n) => !floorId || n?.floors.includes(floorId)));
</script>

<g transform={transform.toString()}>
	{#if nodes}
		{#each selectedNodes as n (n.id)}
			<NodeMarker {n} {deviceId} {nodeId} {floor} {onhovered} {onselected} />
		{/each}
	{/if}
</g>
