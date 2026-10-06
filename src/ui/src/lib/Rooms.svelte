<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { config } from '$lib/stores';
	import Room from './Room.svelte';

	interface Props {
		floorId?: string | null;
		transform?: any;
	}

	let { floorId = null, transform = zoomIdentity }: Props = $props();

	let floor = $derived($config?.floors.find((f) => f.id === floorId));
</script>

<g transform={transform.toString()}>
	{#each floor?.rooms ?? [] as room}
		<Room {room} />
	{/each}
</g>
