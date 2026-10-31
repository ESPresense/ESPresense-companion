<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { getLayerCakeContext } from 'layercake';
	import { ellipsePoints } from '$lib/explain';
	import type { ExplainedEllipse, MapScales } from '$lib/types';

	const chart = getLayerCakeContext<MapScales>();

	let { transform = zoomIdentity, ellipse }: { transform?: any; ellipse: ExplainedEllipse } = $props();

	let points = $derived(
		ellipsePoints(ellipse)
			.map(([x, y]) => `${chart.xScale(x)},${chart.yScale(y)}`)
			.join(' ')
	);
</script>

<g transform={transform.toString()}>
	<polygon {points} fill="white" fill-opacity="0.08" stroke="white" stroke-opacity="0.6" stroke-dasharray="4 3" pointer-events="none" data-testid="fix-ellipse" />
</g>
