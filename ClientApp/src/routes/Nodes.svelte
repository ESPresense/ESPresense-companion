<script lang="ts">
  import { getContext } from 'svelte';
  import { config, devices } from '../lib/stores';
  import { scaleOrdinal, schemeCategory10 } from "d3";
  import type { Config, Node, Room, Device } from '../lib/types';

  const { data, x, xScale, y, yScale } = getContext('LayerCake');

  export let r = 5;
  export let radarId:string = "";
  export let floor = 0;

  let radar: Device | undefined;
  $: radar = $devices?.find(n => n.id == radarId);
  let colors = getContext('colors');
  $: floorId = $config?.floors[floor]?.id;
  $: console.log("floorId", floorId);
  let nodes: Node[] | undefined;
  $: nodes = $config?.nodes?.filter(n => n.floors.includes(floorId));
  </script>

{#if nodes }
  {#each nodes as n}
    <circle cx='{ $xScale(n.point[0]) }' cy='{ $yScale(n.point[1]) }' fill={colors(n.id)} {r} />
    <text x='{ $xScale(n.point[0])  + 7}' y='{ $yScale(n.point[1])  + 3.5}' fill='white' font-size='10px'>{n.name}</text>
    {#if radar?.nodes && radar.nodes[n.id] }
      <ellipse cx='{ $xScale(n.point[0]) }' cy='{ $yScale(n.point[1]) }' fill={colors(n.id)} stroke={colors(n.id)} fill-opacity='0.1' rx='{Math.abs($xScale(0) - $xScale(radar.nodes[n.id]))}' ry='{Math.abs($yScale(0) - $yScale(radar.nodes[n.id]))}' />
      <text x='{ $xScale(n.point[0])}' y='{ $yScale(n.point[1] + radar.nodes[n.id]/2)}' fill={colors(n.id)} font-size='10px'>{radar.nodes[n.id]}</text>
    {/if}
  {/each}
{/if}