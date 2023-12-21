<script lang="ts">
	import { createEventDispatcher, getContext } from 'svelte';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { devices, relative } from '$lib/stores';

	import type { Node, Device, Floor, LayerCakeContext } from '$lib/types';
	import type { ScaleOrdinal } from 'd3';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');
  const colors = getContext<ScaleOrdinal<string, string>>('colors');

	const r = tweened(0, { duration: 100, easing: cubicOut });
	const v = tweened(0, { duration: 1000, easing: cubicOut });

	export let radarDevice: Device | undefined = undefined;
	export let radarNode: Node | undefined = undefined;
	export let floor: Floor | undefined = undefined;
	export let n: Node;

	$: radarDist = radarDevice?.nodes[n.id]?.dist || radarNode?.nodes[n.id]?.dist;
	$: radarVar = radarDevice?.nodes[n.id]?.var || radarNode?.nodes[n.id]?.var;
	$: radarLastHit = radarDevice?.nodes[n.id]?.lh ?? radarNode?.nodes[n.id]?.lh ?? 0;
	$: radarTimeout = radarDevice?.timeout || 30;
	$: v.set(fixRadiusFromHeight(Math.sqrt(radarVar ?? 0)));
	$: r.set(fixRadiusFromHeight(radarDist));
	let hovered = '';
	let selected = '';

	let dispatcher = createEventDispatcher();

	let innerStop: number = 0;
	let outerStop: number = 1;
	let hit: number = 1;

	$: innerStop = 0.5 * ($r / ($r + $v));
	$: outerStop = 1 - innerStop;
	$: hit = Math.min(1, Math.max(0, radarTimeout - radarLastHit) / radarTimeout);
	$: hr = hit * $r;

	function fixRadiusFromHeight(dr: number | undefined): number {
		if (dr == undefined) return 0;
		var nz = n.point[2];
		var dz = floor ? (floor?.bounds[1][2] - floor?.bounds[0][2]) / 2.0 : 2.5;
		var heightDifference = dz - nz;
		if (Math.abs(heightDifference) > dr) return dr;
		var radius = Math.sqrt(Math.pow(dr, 2) - Math.pow(heightDifference, 2));
		return radius;
	}

	function hover(n: Node | null) {
		hovered = n?.id ?? '';
		dispatcher('hovered', n);
	}

	function unselect() {}

	function select(n: Node) {
		selected = n?.id ?? '';
		dispatcher('selected', n);
	}
</script>

<defs>
	{#if $r > 0}
		<radialGradient id="variance-gradient-{n.id}" cx="50%" cy="50%" r={innerStop} gradientUnits="objectBoundingBox">
			<stop offset="0" style="stop-color:{colors(n.id)}; stop-opacity:0" />
			<stop offset={innerStop} style="stop-color:{colors(n.id)}; stop-opacity:0" />
			<stop offset="0.5" style="stop-color:{colors(n.id)}; stop-opacity:1" />
			<stop offset={outerStop} style="stop-color:{colors(n.id)}; stop-opacity:0" />
			<stop offset="1" style="stop-color:{colors(n.id)}; stop-opacity:0" />
		</radialGradient>
	{/if}
</defs>

<path
  d="M{$xScale(n.point[0])},{$yScale(n.point[1])} m -5,0 5,-5 5,5 -5,5 z"
  fill={colors(n.id)}
  role="figure"
  on:mouseover={() => { hover(n); }} on:mouseout={() => { hover(null); }}
  on:focus={() => { select(n); }} on:blur={() => { unselect(); }}
/>
<text x={$xScale(n.point[0]) + 7} y={$yScale(n.point[1]) + 3.5} fill="white" font-size="10px">{n.name}</text>
{#if radarDist && $r > 0}
	<ellipse cx={$xScale(n.point[0])} cy={$yScale(n.point[1])} fill="none" stroke={colors(n.id)} rx={Math.abs($xScale(0) - $xScale($r))} ry={Math.abs($yScale(0) - $yScale($r))} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
	<ellipse cx={$xScale(n.point[0])} cy={$yScale(n.point[1])} fill={`url(#variance-gradient-${n.id})`} rx={2 * Math.abs($xScale(0) - $xScale($r + $v))} ry={2 * Math.abs($yScale(0) - $yScale($r + $v))} fill-opacity="0.5" />
	<g>
		<line x1={$xScale(n.point[0] - hr)} y1={$yScale(n.point[1])} x2={$xScale(n.point[0] + hr)} y2={$yScale(n.point[1])} stroke={colors(n.id)} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
		<line x1={$xScale(n.point[0])} y1={$yScale(n.point[1] - hr)} x2={$xScale(n.point[0])} y2={$yScale(n.point[1] + hr)} stroke={colors(n.id)} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
		<text x={$xScale(n.point[0])} y={$yScale(n.point[1]) + 15} fill="white" font-size="10px">
			{#if radarVar !== null && radarVar !== undefined}
				{radarDist.toFixed(2) ?? 'N/A'} ± {Math.sqrt(radarVar).toFixed(2)}
			{:else}
				{radarDist.toFixed(2) ?? 'N/A'}
			{/if}
		</text>
	</g>
{/if}
