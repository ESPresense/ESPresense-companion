<script lang="ts">
	import Map from '#lib/Map.svelte';
	import FloorTabs from '#lib/FloorTabs.svelte';
	import BackgroundUpload from '#lib/BackgroundUpload.svelte';
	import NodeLinkControls from '#lib/NodeLinkControls.svelte';
	import type { LinkDirection } from '#lib/nodeLinks.js';
	import { gotoDetail } from '#lib/urls.js';

	// Local state, not a prop: SvelteKit route components only receive `data` and `form`.
	let floorId: string | null = $state(null);

	let showLinks = $state(false);
	let nearest: number | null = $state(4);
	let direction: LinkDirection = $state('all');
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-surface-50-950">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<NodeLinkControls bind:enabled={showLinks} bind:nearest bind:direction />
	<Map onselected={(item) => gotoDetail(item)} bind:floorId links={showLinks ? { nearest, direction } : null} />
</div>
