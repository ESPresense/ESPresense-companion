<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { getLayerCakeContext } from 'layercake';
	import { calibration, nodes } from '$lib/stores';
	import { errorColor } from '$lib/explain';
	import type { MapScales } from '$lib/types';

	const chart = getLayerCakeContext<MapScales>();

	let { transform = zoomIdentity, nodeId, floorId = null }: { transform?: any; nodeId: string; floorId?: string | null } = $props();

	let selected = $derived($nodes?.find((n) => n.id === nodeId));

	// The matrix is keyed by node name, transmitter first; fall back to the other direction
	let pairs = $derived.by(() => {
		if (!selected?.location) return [];
		const name = selected.name ?? selected.id;
		const matrix = $calibration?.matrix ?? {};
		return ($nodes ?? []).flatMap((other) => {
			const otherName = other.name ?? other.id;
			const link = matrix[name]?.[otherName] ?? matrix[otherName]?.[name];
			if (other.id === nodeId || !other.location || link?.distance == null || link?.mapDistance == null) return [];
			if (floorId && !other.floors?.includes(floorId)) return [];
			return [{ id: other.id, location: other.location, measured: link.distance, map: link.mapDistance, percent: link.percent ?? 0 }];
		});
	});
</script>

{#if selected?.location}
	<g transform={transform.toString()} pointer-events="none" data-testid="node-pairs">
		{#each pairs as p (p.id)}
			{@const [x1, y1] = [chart.xScale(selected.location.x), chart.yScale(selected.location.y)]}
			{@const [x2, y2] = [chart.xScale(p.location.x), chart.yScale(p.location.y)]}
			<line {x1} {y1} {x2} {y2} stroke={errorColor(p.percent)} stroke-width="2" stroke-opacity="0.8" />
			<text x={(x1 + x2) / 2} y={(y1 + y2) / 2} fill="white" font-size="10px" text-anchor="middle">{p.measured.toFixed(1)} / {p.map.toFixed(1)}</text>
		{/each}
	</g>
{/if}
