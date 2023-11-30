<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config } from '$lib/stores';
	import Room from './Room.svelte';

	export let floorId: string | null = null;
	export let transform = zoomIdentity;

	$: floor = $config?.floors.find((f) => f.id === floorId);
</script>

<g transform={transform.toString()}>
	{#each floor?.rooms ?? [] as room}
		<Room {room} />
	{/each}
</g>
