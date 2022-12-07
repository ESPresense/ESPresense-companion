<script>
  import { getContext } from 'svelte';
  import { scaleCanvas } from 'layercake';
  import { config, devices, nodes } from '../lib/stores';
  import Room from './Room.svelte';

  export let floor = 0;

  const { data, xGet, yGet, width, height } = getContext('LayerCake');
$: console.log('floor', floor);
</script>
{#each $config.floors[floor]?.rooms ?? [] as room}
  <Room {room} />
{/each}