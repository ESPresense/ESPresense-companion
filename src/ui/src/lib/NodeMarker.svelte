<script lang="ts">
	import { getContext } from 'svelte';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { devices, nodes, relative } from '$lib/stores';

	import type { Node, Floor, LayerCakeContext } from '$lib/types';
	import type { ScaleOrdinal } from 'd3';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');
	const colors = getContext<ScaleOrdinal<string, string>>('colors');

	const r = tweened(0, { duration: 100, easing: cubicOut });
	const v = tweened(0, { duration: 1000, easing: cubicOut });

	export let deviceId: string | null = null;
	export let nodeId: string | null = null;
	export let floor: Floor | undefined = undefined;
	export let n: Node;
	export let onhovered: ((node: Node | null) => void) | undefined = undefined;
	export let onselected: ((node: Node) => void) | undefined = undefined;

	$: radarDevice = $devices?.find((d) => d.id === deviceId);
	$: radarNode = $nodes?.find((d) => d.id === nodeId);
	$: radarDist = radarDevice?.nodes[n.id]?.dist || radarNode?.nodes[n.id]?.dist;
	$: radarVar = radarDevice?.nodes[n.id]?.var || radarNode?.nodes[n.id]?.var;
	$: radarLastHit = $relative - (radarDevice?.nodes[n.id]?.lh || radarNode?.nodes[n.id]?.lh || 0);
	$: radarTimeout = radarDevice?.timeout || 30000;
	$: v.set(fixRadiusFromHeight(Math.sqrt(radarVar ?? 0)));
	$: r.set(fixRadiusFromHeight(radarDist));

	let innerStop: number = 0;
	let outerStop: number = 1;
	let hit: number = 1;

	$: innerStop = 0.5 * ($r / ($r + $v));
	$: outerStop = 1 - innerStop;
	$: hit = Math.min(1, Math.max(0, radarTimeout - radarLastHit) / radarTimeout);
	$: hr = hit * $r;

	function fixRadiusFromHeight(dr: number | undefined): number {
		if (dr == undefined) return 0;
		var nz = n.location.z;
		var dz = floor ? (floor?.bounds[1][2] - floor?.bounds[0][2]) / 2.0 : 2.5;
		var heightDifference = dz - nz;
		if (Math.abs(heightDifference) > dr) return dr;
		var radius = Math.sqrt(Math.pow(dr, 2) - Math.pow(heightDifference, 2));
		return radius;
	}

	function hover(n: Node | null) {
		onhovered?.(n);
	}

	function unselect() {}

	function select(n: Node) {
		onselected?.(n);
	}
</script>

<defs>
	{#if radarDist && $r > 0}
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
	d="M{$xScale(n.location.x)},{$yScale(n.location.y)} m -5,0 5,-5 5,5 -5,5 z"
	fill={colors(n.id)}
	role="figure"
	onmouseover={() => {
		hover(n);
	}}
	onmouseout={() => {
		hover(null);
	}}
	onfocus={() => {
		select(n);
	}}
	onblur={() => {
		unselect();
	}}
/>
<text x={$xScale(n.location.x) + 7} y={$yScale(n.location.y) + 3.5} fill="white" font-size="10px">{n.name}</text>
{#if radarDist && $r > 0}
	<text x={$xScale(n.location.x)} y={$yScale(n.location.y) + 15} fill="white" font-size="10px">
		{#if radarVar !== null && radarVar !== undefined}
			{radarDist.toFixed(2) ?? 'n/a'} ± {Math.sqrt(radarVar).toFixed(2)}
		{:else}
			{radarDist.toFixed(2) ?? 'n/a'}
		{/if}
	</text>
	<ellipse style="pointer-events: none" cx={$xScale(n.location.x)} cy={$yScale(n.location.y)} fill="none" stroke={colors(n.id)} rx={Math.abs($xScale(0) - $xScale($r))} ry={Math.abs($yScale(0) - $yScale($r))} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
	<ellipse style="pointer-events: none" cx={$xScale(n.location.x)} cy={$yScale(n.location.y)} fill={`url(#variance-gradient-${n.id})`} rx={2 * Math.abs($xScale(0) - $xScale($r + $v))} ry={2 * Math.abs($yScale(0) - $yScale($r + $v))} fill-opacity="0.5" />
	<line x1={$xScale(n.location.x - hr)} y1={$yScale(n.location.y)} x2={$xScale(n.location.x + hr)} y2={$yScale(n.location.y)} stroke={colors(n.id)} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
	<line x1={$xScale(n.location.x)} y1={$yScale(n.location.y - hr)} x2={$xScale(n.location.x)} y2={$yScale(n.location.y + hr)} stroke={colors(n.id)} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
{/if}
