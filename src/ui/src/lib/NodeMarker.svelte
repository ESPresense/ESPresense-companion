<script lang="ts">
	import { getContext } from 'svelte';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { devices, nodes, relative } from '$lib/stores';

	import { getLayerCakeContext } from 'layercake';
	import { errorColor } from '$lib/explain';
	import type { ExplainedNode, Node, Floor, MapScales } from '$lib/types';
	import type { ScaleOrdinal } from 'd3';

	const chart = getLayerCakeContext<MapScales>();
	const colors = getContext<ScaleOrdinal<string, string>>('colors');

	const r = tweened(0, { duration: 100, easing: cubicOut });
	const v = tweened(0, { duration: 1000, easing: cubicOut });

	interface Props {
		deviceId?: string | null;
		nodeId?: string | null;
		floor?: Floor | undefined;
		n: Node;
		fix?: ExplainedNode | undefined;
		fixScale?: number;
		onhovered?: ((node: Node | null) => void) | undefined;
		onselected?: ((node: Node) => void) | undefined;
	}

	let { deviceId = null, nodeId = null, floor = undefined, n, fix = undefined, fixScale = 1, onhovered = undefined, onselected = undefined }: Props = $props();

	let innerStop: number = $derived(0.5 * ($r / ($r + $v)));
	let outerStop: number = $derived(1 - innerStop);

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
	let radarDevice = $derived($devices?.find((d) => d.id === deviceId));
	let radarNode = $derived($nodes?.find((d) => d.id === nodeId));
	// With an explanation, show what the locator used: the fit scales geometric distance to meet the
	// measured one, so its circle is the measured distance back in map metres
	let radarDist = $derived(fix ? (fix.used ? (fix.measured ?? 0) / fixScale : undefined) : radarDevice?.nodes[n.id]?.dist || radarNode?.nodes[n.id]?.dist);
	let radarVar = $derived(radarDevice?.nodes[n.id]?.var || radarNode?.nodes[n.id]?.var);
	let radarLastHit = $derived($relative - (radarDevice?.nodes[n.id]?.lh || radarNode?.nodes[n.id]?.lh || 0));
	let radarTimeout = $derived(radarDevice?.timeout || 30000);
	let hit: number = $derived(fix ? 1 : Math.min(1, Math.max(0, radarTimeout - radarLastHit) / radarTimeout));
	let color = $derived(!fix ? colors(n.id) : !fix.used ? '#777' : fix.residual != null ? errorColor(fix.residual / Math.max(fix.fitted ?? 1, 1)) : colors(n.id));
	$effect(() => {
		v.set(fixRadiusFromHeight(Math.sqrt(radarVar ?? 0)));
	});
	$effect(() => {
		r.set(fixRadiusFromHeight(radarDist));
	});
	let hr = $derived(hit * $r);
</script>

<defs>
	{#if radarDist && $r > 0}
		<radialGradient id="variance-gradient-{n.id}" cx="50%" cy="50%" r={innerStop} gradientUnits="objectBoundingBox">
			<stop offset="0" style="stop-color:{color}; stop-opacity:0" />
			<stop offset={innerStop} style="stop-color:{color}; stop-opacity:0" />
			<stop offset="0.5" style="stop-color:{color}; stop-opacity:1" />
			<stop offset={outerStop} style="stop-color:{color}; stop-opacity:0" />
			<stop offset="1" style="stop-color:{color}; stop-opacity:0" />
		</radialGradient>
	{/if}
</defs>

<path
	d="M{chart.xScale(n.location.x)},{chart.yScale(n.location.y)} m -5,0 5,-5 5,5 -5,5 z"
	fill={color}
	role="figure"
	aria-label={`Select node ${n.name}`}
	onclick={() => {
		select(n);
	}}
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
<text x={chart.xScale(n.location.x) + 7} y={chart.yScale(n.location.y) + 3.5} fill="white" font-size="10px">{n.name}</text>
{#if radarDist && $r > 0}
	<text x={chart.xScale(n.location.x)} y={chart.yScale(n.location.y) + 15} fill="white" font-size="10px">
		{#if radarVar !== null && radarVar !== undefined}
			{radarDist.toFixed(2) ?? 'n/a'} ± {Math.sqrt(radarVar).toFixed(2)}
		{:else}
			{radarDist.toFixed(2) ?? 'n/a'}
		{/if}
		{#if fix?.residual != null}
			({fix.residual >= 0 ? '+' : ''}{fix.residual.toFixed(2)})
		{/if}
	</text>
	<ellipse style="pointer-events: none" cx={chart.xScale(n.location.x)} cy={chart.yScale(n.location.y)} fill="none" stroke={color} rx={Math.abs(chart.xScale(0) - chart.xScale($r))} ry={Math.abs(chart.yScale(0) - chart.yScale($r))} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
	<ellipse style="pointer-events: none" cx={chart.xScale(n.location.x)} cy={chart.yScale(n.location.y)} fill={`url(#variance-gradient-${n.id})`} rx={2 * Math.abs(chart.xScale(0) - chart.xScale($r + $v))} ry={2 * Math.abs(chart.yScale(0) - chart.yScale($r + $v))} fill-opacity="0.5" />
	<line x1={chart.xScale(n.location.x - hr)} y1={chart.yScale(n.location.y)} x2={chart.xScale(n.location.x + hr)} y2={chart.yScale(n.location.y)} stroke={color} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
	<line x1={chart.xScale(n.location.x)} y1={chart.yScale(n.location.y - hr)} x2={chart.xScale(n.location.x)} y2={chart.yScale(n.location.y + hr)} stroke={color} stroke-width="2" stroke-opacity={0.25 + 0.75 * hit} />
{/if}
