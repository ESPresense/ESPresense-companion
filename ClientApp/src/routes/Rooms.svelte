<script>
  import { config } from '../lib/stores';
  import Room from './Room.svelte';
  export let floor = 0;
</script>
{#each $config.floors[floor]?.rooms ?? [] as room}
  <Room {room} />
{/each}