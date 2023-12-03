<script lang="ts">
	import { getContext } from 'svelte';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { devices } from '$lib/stores';

	import type { Node, Device } from '$lib/types';
	import type { ScaleOrdinal } from 'd3';

	const { xScale, yScale } = getContext('LayerCake');

	const r = tweened(0, { duration: 100, easing: cubicOut });

	export let radarId: string | undefined = null;
	export let n: Node;
  export let floor: Floor | undefined = null;

	let radar: Device | undefined;
	$: radar = $devices?.find((n) => n.id == radarId);
  let radarDist: number | undefined;
  $: radarDist = radar?.nodes[n.id]?.dist;
	$: r.set(fixRadiusFromHeight(radarDist));
	let colors: ScaleOrdinal<string, string> = getContext('colors');

	function fixRadiusFromHeight(dr: number | undefined): number {
		if (dr == undefined) return 0;
		var nz = n.point[2];
		var dz = (floor.bounds[1][2]-floor.bounds[0][2])/2.0;
		var heightDifference = dz - nz;
		if (Math.abs(heightDifference) > dr) return 0;
		var radius = Math.sqrt(Math.pow(dr, 2) - Math.pow(heightDifference, 2));
		return radius;
	}

	function errorBarLength(dr: number, variance: number): { x: number; y: number } {
		return { x: variance, y: variance };
	}
</script>

<defs>
	{#if $r > 0 && radar && radar.nodes[n.id]}
		<radialGradient id="variance-gradient-{n.id}" cx={$xScale(n.point[0])} cy={$yScale(n.point[1])} r={$xScale($r + radar.nodes[n.id]?.var)} gradientUnits="userSpaceOnUse">
			<!-- Transparent stop at the center minus variance -->
			<stop offset={($r - radar.nodes[n.id]?.var) / ($r + radar.nodes[n.id]?.var)} style="stop-color:{colors(n.id)}; stop-opacity:0" />
			<!-- Opaque stop at the center -->
			<stop offset={$r / ($r + radar.nodes[n.id]?.var)} style="stop-color:{colors(n.id)}; stop-opacity:1" />
			<!-- Transparent stop at the center plus variance -->
			<stop offset={($r + radar.nodes[n.id]?.var) / ($r + radar.nodes[n.id]?.var)} style="stop-color:{colors(n.id)}; stop-opacity:0" />
		</radialGradient>
	{/if}
</defs>

<path d="M{$xScale(n.point[0])},{$yScale(n.point[1])} m -5,0 5,-5 5,5 -5,5 z" fill={colors(n.id)} />
<text x={$xScale(n.point[0]) + 7} y={$yScale(n.point[1]) + 3.5} fill="white" font-size="10px">{n.name}</text>
{#if $r > 0 && radar && radar.nodes[n.id]}
	<ellipse cx={$xScale(n.point[0])} cy={$yScale(n.point[1])} fill={`url(#variance-gradient-${n.id})`} rx={Math.abs($xScale(0) - $xScale($r + radar.nodes[n.id]?.var))} ry={Math.abs($yScale(0) - $yScale($r + radar.nodes[n.id]?.var))} fill-opacity="0.5" />
	<ellipse cx={$xScale(n.point[0])} cy={$yScale(n.point[1])} fill="none" stroke={colors(n.id)} rx={Math.abs($xScale(0) - $xScale($r))} ry={Math.abs($yScale(0) - $yScale($r))} stroke-width="2" />
	<g>
		<line x1={$xScale(n.point[0] - errorBarLength(radar.nodes[n.id]?.dist, radar.nodes[n.id]?.var).x)} y1={$yScale(n.point[1])} x2={$xScale(n.point[0] + errorBarLength(radar.nodes[n.id]?.dist, radar.nodes[n.id]?.var).x)} y2={$yScale(n.point[1])} stroke={colors(n.id)} stroke-width="2" />
		<line x1={$xScale(n.point[0])} y1={$yScale(n.point[1] - errorBarLength(radar.nodes[n.id]?.dist, radar.nodes[n.id]?.var).y)} x2={$xScale(n.point[0])} y2={$yScale(n.point[1] + errorBarLength(radar.nodes[n.id]?.dist, radar.nodes[n.id]?.var).y)} stroke={colors(n.id)} stroke-width="2" />
		<text x={$xScale(n.point[0])} y={$yScale(n.point[1]) + 15} fill="white" font-size="10px">
			{#if radar.nodes[n.id]?.var !== null && radar.nodes[n.id]?.var !== undefined}
				{radar.nodes[n.id]?.dist.toFixed(2) ?? 'N/A'} ± {radar.nodes[n.id]?.var.toFixed(2)}
			{:else}
				{radar.nodes[n.id]?.dist.toFixed(2) ?? 'N/A'}
			{/if}
		</text>
	</g>
{/if}
