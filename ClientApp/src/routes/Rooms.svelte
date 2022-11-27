<script>
  import { getContext } from 'svelte';
  import { scaleCanvas } from 'layercake';
  import { config, devices, nodes } from '../lib/stores';
  import Room from './Room.svelte';

  const { data, xGet, yGet, width, height } = getContext('LayerCake');

</script>

{#each $config.floors[0].rooms as room}
  <Room {room} />
{/each}