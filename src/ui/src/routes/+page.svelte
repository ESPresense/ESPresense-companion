<script lang="ts">
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';
	import BackgroundUpload from '$lib/BackgroundUpload.svelte';
	import { detail } from '$lib/urls';

	export let floorId: string | null = null;
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-slate-900">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<Map on:selected={(d) => detail(d.detail)} bind:floorId />
</div>
