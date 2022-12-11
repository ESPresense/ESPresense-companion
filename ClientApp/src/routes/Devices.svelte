<script lang="ts">
  import { getContext, createEventDispatcher } from 'svelte';
  import { config, devices } from '../lib/stores';
  import type { Config, Device, Node, Room } from '../lib/types';
  import { tweened } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import { filter } from '@skeletonlabs/skeleton';

	const { data, x, xScale, y, yScale } = getContext('LayerCake');

	const r = tweened(5, {
		duration: 400,
		easing: cubicOut
	});

  let colors = getContext('colors');

  export let floor = 0;
  $: floorId = $config?.floors[floor]?.id;

	let hovered = "";
	let selected = "";

	let dispatcher = createEventDispatcher();

	function hover (d:Device) {
		r.set(d == null ? 5 : 10);
		hovered = d?.id;
		dispatcher('hovered', d);
	}

	function select (d:Device) {
		selected = d?.id;
		dispatcher('selected', d);
	}
  </script>

  <g>
	{#if $devices }
	{#each $devices.filter(a => (a?.room?.floor ?? floorId) == floorId) as d}
	  <circle cx='{ $xScale(d.location.x) }' cy='{ $yScale(d.location.y) }' fill={d?.room?.id ? colors(d?.room?.id) : "black"} r={ d.id == hovered ? $r : 5 } on:mouseover="{() => { hover(d) }}" on:focus="{() => { select(d) }}" on:mouseout="{() => { hover(null) }}" />
	  <text x='{ $xScale(d.location.x) + 7}' y='{ $yScale(d.location.y) + 3 }' fill='white' font-size='10px'>{d.id}</text>
	{/each}
	{/if}
  </g>