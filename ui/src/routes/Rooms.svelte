<script>
  import { zoomIdentity } from 'd3-zoom';
  import { config } from '../lib/stores';
  import Room from './Room.svelte';

  export let floor = 0;
  export let transform = zoomIdentity;
</script>

<g transform={transform.toString()}>
  {#each $config.floors[floor]?.rooms ?? [] as room}
    <Room {room} />
  {/each}
</g>