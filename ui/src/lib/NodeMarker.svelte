<script lang="ts">
	import { getContext } from 'svelte';
	import { tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';

	import { devices } from '$lib/stores';

	import type { Node, Device } from '$lib/types';
	import type { ScaleOrdinal } from 'd3';

	const { xScale, yScale } = getContext('LayerCake');

	const r = tweened(0, { duration: 100, easing: cubicOut });

	export let radarId: string | null = null;
	export let n: Node;

	let radar: Device | undefined;
	$: radar = $devices?.find((n) => n.id == radarId);
	$: r.set(radiusOnIntersectionCircle(radar, n));
	let colors: ScaleOrdinal<string, string> = getContext('colors');

	function radiusOnIntersectionCircle(d: Device | undefined, n: Node): number {
		if (d == undefined) return 0;
		var dr = d.nodes[n.id];
		if (dr == undefined) return 0;
		var nz = n.point[2];
		var dz = d.location.z;
		var heightDifference = dz - nz;

		// Check if the plane intersects the sphere
		if (Math.abs(heightDifference) > dr) return 0;

		var radius = Math.sqrt(Math.pow(dr, 2) - Math.pow(heightDifference, 2));
		return radius;
}
</script>

<path d="M{$xScale(n.point[0])},{$yScale(n.point[1])} m -5,0 5,-5 5,5 -5,5 z" fill={colors(n.id)} />
<text x='{ $xScale(n.point[0])  + 7}' y='{ $yScale(n.point[1])  + 3.5}' fill='white' font-size='10px'>{n.name}</text>
{#if $r > 0}
	<ellipse cx='{ $xScale(n.point[0]) }' cy='{ $yScale(n.point[1]) }' fill={colors(n.id)} stroke={colors(n.id)} fill-opacity='0.1' rx='{Math.abs($xScale(0) - $xScale($r))}' ry='{Math.abs($yScale(0) - $yScale($r))}' />
	<text x='{ $xScale(n.point[0] - $r/2)}' y='{ $yScale(n.point[1] + $r/2)}' fill={colors(n.id)} font-size='12px'>{ radar?.nodes[n.id] ?? "" }</text>
	<text x='{ $xScale(n.point[0] + $r/2)}' y='{ $yScale(n.point[1] - $r/2)}' fill={colors(n.id)} font-size='12px'>{ radar?.nodes[n.id] ?? "" }</text>
{/if}
