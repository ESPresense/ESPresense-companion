<script lang="ts">
	import { getContext } from 'svelte';
	import { spring, tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { config, devices } from '../lib/stores';

	import type { Node, Device } from '../lib/types';
	import type { ScaleOrdinal } from 'd3';

	const { xScale, yScale } = getContext('LayerCake');

	const r = tweened(0, { duration: 100, easing: cubicOut });

	export let radarId: string = '';
	export let n: Node;

	let radar: Device | undefined;
	$: radar = $devices?.find((n) => n.id == radarId);
	$: r.set(radar?.nodes[n.id] ?? 0);
	let colors: ScaleOrdinal<string, string> = getContext('colors');
</script>

<path d="M{$xScale(n.point[0])},{$yScale(n.point[1])} m -5,0 5,-5 5,5 -5,5 z" fill={colors(n.id)} />
<text x='{ $xScale(n.point[0])  + 7}' y='{ $yScale(n.point[1])  + 3.5}' fill='white' font-size='10px'>{n.name}</text>
{#if $r > 0}
  <ellipse cx='{ $xScale(n.point[0]) }' cy='{ $yScale(n.point[1]) }' fill={colors(n.id)} stroke={colors(n.id)} fill-opacity='0.1' rx='{Math.abs($xScale(0) - $xScale($r))}' ry='{Math.abs($yScale(0) - $yScale($r))}' />
  <text x='{ $xScale(n.point[0])}' y='{ $yScale(n.point[1] + $r/2)}' fill={colors(n.id)} font-size='10px'>{ radar?.nodes[n.id] ?? "" }</text>
{/if}
