<script>
	// Import the getContext function from svelte
	import { getContext } from 'svelte';
	import { config, devices, nodes } from '../lib/stores';

	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	export let fill = 'white';
	export let r = 5;
	export let radar = null;

	$: console.log(radar);
  </script>

  <g>
	{#if $nodes }
	{#each $nodes as n}
	  <circle cx='{ $xScale(n.location.x) }' cy='{ $yScale(n.location.y) }' {fill} {r} />
	  <text x='{ $xScale(n.location.x)  + 7}' y='{ $yScale(n.location.y)  + 3.5}' fill='red' font-size='10px'>{n.id}</text>
  	  {#if radar?.nodes && radar.nodes[n.id] }
		<ellipse cx='{ $xScale(n.location.x) }' cy='{ $yScale(n.location.y) }' stroke="white" fill-opacity=0 rx={$xScale(radar.nodes[n.id]/2)} ry={$yScale(radar.nodes[n.id]/2)} />
	  {/if}
	{/each}
	{/if}
  </g>