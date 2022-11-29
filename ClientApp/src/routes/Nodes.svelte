<script>
	// Import the getContext function from svelte
	import { getContext } from 'svelte';
	import { config, devices, nodes } from '../lib/stores';
  import { scaleOrdinal, schemeCategory10 } from "d3";

	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	export let r = 5;
	export let radar = null;

  let myColor = scaleOrdinal(schemeCategory10);
  </script>

{#if $nodes }
	{#each $nodes as n}
    <circle cx='{ $xScale(n.location.x) }' cy='{ $yScale(n.location.y) }' fill={myColor(n)} {r} />
    <text x='{ $xScale(n.location.x)  + 7}' y='{ $yScale(n.location.y)  + 3.5}' fill='white' font-size='10px'>{n.id}</text>
    {#if radar?.nodes && radar.nodes[n.id] }
      <ellipse cx='{ $xScale(n.location.x) }' cy='{ $yScale(n.location.y) }' fill={myColor(n)} stroke={myColor(n)} fill-opacity='0.1' rx='{Math.abs($xScale(0) - $xScale(radar.nodes[n.id]))}' ry='{Math.abs($yScale(0) - $yScale(radar.nodes[n.id]))}' />
      <text x='{ $xScale(n.location.x)}' y='{ $yScale(n.location.y + radar.nodes[n.id]/2)}' fill={myColor(n)} font-size='10px'>{radar.nodes[n.id]}</text>
    {/if}
	{/each}
{/if}