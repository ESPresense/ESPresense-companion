<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import type { LayerCakeContext } from '$lib/types';

	export let transform = zoomIdentity;
	$: cursorX = 0;
	$: cursorY = 0;

	const { xScale, yScale, width, height, padding } = getContext<any>('LayerCake');

	function updateCoordinates(event: MouseEvent) {
		if (!$xScale || !$yScale) return;

		const target = event.target as SVGElement;
		const svg = target.ownerSVGElement || target as SVGSVGElement;
		const point = svg.createSVGPoint();
		point.x = event.clientX;
		point.y = event.clientY;

		// Convert screen coordinates to SVG coordinates
		const svgPoint = point.matrixTransform(svg.getScreenCTM()?.inverse());

		// Adjust for padding from context
		const adjustedX = svgPoint.x - $padding.left;
		const adjustedY = svgPoint.y - $padding.top;

		const transformedX = (adjustedX - transform.x) / transform.k;
		const transformedY = (adjustedY - transform.y) / transform.k;

		cursorX = $xScale.invert(transformedX);
		cursorY = $yScale.invert(transformedY);
	}
</script>

<svelte:window on:mousemove={updateCoordinates} />

<g transform="translate({$width - 120}, {$height - 40})">
	<rect
		width="110"
		height="30"
		rx="4"
		fill="#2563eb"
		class="shadow-lg"
	/>
	<text
		x="55"
		y="19"
		text-anchor="middle"
		fill="white"
		font-size="12"
	>
		X: {cursorX.toFixed(2)}, Y: {cursorY.toFixed(2)}
	</text>
</g>
