<script>
	import { getContext, createEventDispatcher } from 'svelte';
	import { config, devices, nodes } from '../lib/stores';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	const r = tweened(5, {
		duration: 400,
		easing: cubicOut
	});

	export let fill = '#000';

	let hovered = "";
	let selected = "";

	let dispatcher = createEventDispatcher();

function hover (d) {
	r.set(d == null ? 5 : 10);
	hovered = d?.id;
	dispatcher('hovered', d);
}

function select (d) {
	selected = d?.id;
	dispatcher('selected', d);
}
  </script>

  <g>
	{#if $devices }
	{#each $devices as d}
	  <circle cx='{ $xScale(d.location.x) }' cy='{ $yScale(d.location.y) }' {fill} r={ d.id == hovered ? $r : 5 } on:mouseover="{() => { hover(d) }}" on:focus="{() => { select(d) }}" on:mouseout="{() => { hover(null) }}" />
	  <text x='{ $xScale(d.location.x) + 7}' y='{ $yScale(d.location.y) + 3 }' fill='white' font-size='10px'>{d.id}</text>
	{/each}
	{/if}
  </g>