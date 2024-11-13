<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config } from '$lib/stores';
	import Room from './Room.svelte';
	import type { RoomProps, Room as RoomType } from '$lib/types';

	export let floorId: string | null = null;
	export let transform = zoomIdentity;

	$: floor = $config?.floors.find((f) => f.id === floorId);
	$: roomProps = (room: RoomType): RoomProps => ({
		room,
		wallThickness: floor?.wallThickness ?? 0.1
	});
</script>

<g transform={transform.toString()}>
	{#each floor?.rooms ?? [] as room}
		<Room {...roomProps(room)} />
	{/each}
</g>
