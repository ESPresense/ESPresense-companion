<script lang="ts">
  import { config, devices, showAll } from '../lib/stores';
  import { zoomIdentity } from 'd3-zoom';

  import DeviceDot from './DeviceDot.svelte';

  export let transform = zoomIdentity;
  export let floor = 0;
  $: floorId = $config?.floors[floor]?.id;
  </script>

<g transform={transform.toString()}>
  {#if $devices }
  {#each $devices.filter(a => $showAll || ((a?.floor?.id ?? floorId) == floorId)) as d (d.id)}
    {#if d.confidence > 1 && d.location}
      <DeviceDot { d } on:hovered on:selected />
    {/if}
  {/each}
  {/if}
</g>