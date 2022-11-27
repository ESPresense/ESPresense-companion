<script>
	// Import the getContext function from svelte
	import { getContext } from 'svelte';
	import { config, devices, nodes } from '../lib/stores';

	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	export let fill = 'white';
	export let r = 5;
  </script>

  <g>
	{#if $nodes }
	{#each $nodes as d}
	  <circle cx='{ $xScale(d.location.x) }' cy='{ $yScale(d.location.y) }' {fill} {r} />
	  <text x='{ $xScale(d.location.x)  + 7}' y='{ $yScale(d.location.y)  + 3.5}' fill='red' font-size='10px'>{d.id}</text>
	{/each}
	{/if}
  </g>