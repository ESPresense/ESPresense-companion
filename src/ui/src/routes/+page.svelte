<script lang="ts">
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';
	import BackgroundUpload from '$lib/BackgroundUpload.svelte';
	import { gotoDetail } from '$lib/urls';

	// Local state, not a prop: SvelteKit route components only receive `data` and `form`.
	let floorId: string | null = $state(null);
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-surface-50-950">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<Map onselected={(item) => gotoDetail(item)} bind:floorId />
</div>
