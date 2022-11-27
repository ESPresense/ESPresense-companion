<script>
	// Import the getContext function from svelte
	import { getContext } from 'svelte';
	import { config, devices, nodes } from '../lib/stores';

	// Access the context using the 'LayerCake' keyword
	// Grab some helpful functions
	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	export let fill = '#000';
	export let r = 5;
  </script>

  <g>
	{#if $devices }
	{#each $devices as d}
	  <circle cx='{ $xScale(d.location.x) }' cy='{ $yScale(d.location.y) }' {fill} {r} />
	  <text x='{ $xScale(d.location.x) }' y='{ $yScale(d.location.y) }' fill='white' font-size='10px'>{d.id}</text>
	{/each}
	{/if}
  </g>