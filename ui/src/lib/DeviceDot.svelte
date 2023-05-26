<script lang="ts">
	import { getContext, createEventDispatcher } from 'svelte';
	import { spring, tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import type { ScaleOrdinal } from 'd3';
	import type { Device } from '../lib/types';

	let colors: ScaleOrdinal<string, string> = getContext('colors');

	const { xScale, yScale } = getContext('LayerCake');
	export let d: Device;

	const r = spring(5, { stiffness: 0.15, damping: 0.3 });
	const s = tweened(1, { duration: 500, easing: cubicOut });
	const x = tweened(d.location.x, { duration: 1000, easing: cubicOut });
	const y = tweened(d.location.y, { duration: 1000, easing: cubicOut });

	$: x.set(d.location.x);
	$: y.set(d.location.y);

	let hovered = '';
	let selected = '';

	let dispatcher = createEventDispatcher();

	function hover(d: Device | null) {
		r.set(d == null ? 5 : 10);
		s.set(d == null ? 1 : 2);
		hovered = d?.id ?? '';
		dispatcher('hovered', d);
	}

	function unselect() {}

	function select(d: Device) {
		selected = d?.id ?? '';
		dispatcher('selected', d);
	}
</script>

<circle cx='{ $xScale($x) }' cy='{ $yScale($y) }' fill={d?.room?.id ? colors(d?.room?.id) : "black"} stroke={ d.id == hovered ? 'black' : 'white'} stroke-width={ $s } r={ $r } on:mouseover="{() => { hover(d) }}" on:focus="{() => { select(d) }}" on:mouseout="{() => { hover(null) }}" on:blur="{()=>{unselect()}}" />
<text x='{ $xScale($x) + 7}' y='{ $yScale($y) + 3 }' fill='white' font-size='10px'>{d.name ?? d.id}</text>

