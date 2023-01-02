<script lang="ts">
  import { getContext } from 'svelte';
  import { config, devices } from '../lib/stores';
  import { zoomIdentity } from 'd3-zoom';

  import type { Node, Device } from '../lib/types';
  import type { ScaleOrdinal } from "d3";

  const { xScale, yScale } = getContext('LayerCake');

  export let transform = zoomIdentity;
  export let radarId:string = "";
  export let floor = 0;

  let radar: Device | undefined;
  $: radar = $devices?.find(n => n.id == radarId);
  let colors : ScaleOrdinal<string, string> = getContext('colors');

  $: floorId = $config?.floors[floor]?.id;
  let nodes: Node[] | undefined;
  $: nodes = $config?.nodes?.filter(n => n.floors.includes(floorId));
  </script>

<g transform={transform.toString()}>
{#if nodes }
  {#each nodes as n}
    <path d="M{$xScale(n.point[0])},{$yScale(n.point[1])} m -5,0 5,-5 5,5 -5,5 z" fill={colors(n.id)} />
    <text x='{ $xScale(n.point[0])  + 7}' y='{ $yScale(n.point[1])  + 3.5}' fill='white' font-size='10px'>{n.name}</text>
    {#if radar?.nodes && radar.nodes[n.id] }
      <ellipse cx='{ $xScale(n.point[0]) }' cy='{ $yScale(n.point[1]) }' fill={colors(n.id)} stroke={colors(n.id)} fill-opacity='0.1' rx='{Math.abs($xScale(0) - $xScale(radar.nodes[n.id]))}' ry='{Math.abs($yScale(0) - $yScale(radar.nodes[n.id]))}' />
      <text x='{ $xScale(n.point[0])}' y='{ $yScale(n.point[1] + radar.nodes[n.id]/2)}' fill={colors(n.id)} font-size='10px'>{radar.nodes[n.id]}</text>
    {/if}
  {/each}
{/if}
</g>>