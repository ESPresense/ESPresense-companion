<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import type { LayerCakeContext } from '$lib/types';

	export let transform = zoomIdentity;
	$: cursorX = 0;
	$: cursorY = 0;

	const { xScale, yScale, width, height } = getContext<LayerCakeContext>('LayerCake');

	function updateCoordinates(event: PointerEvent) {
		if (!$xScale || !$yScale) return;

		const svgElement = event.currentTarget as SVGElement;
		const rect = svgElement.getBoundingClientRect();
		const x = event.clientX - rect.left - 16;
		const y = event.clientY - rect.top - 16;

		const transformedX = (x - transform.x) / transform.k;
		const transformedY = (y - transform.y) / transform.k;

		cursorX = $xScale.invert(transformedX);
		cursorY = $yScale.invert(transformedY);
	}
</script>

<rect
	x="0"
	y="0"
	width={$width}
	height={$height}
	fill="transparent"
	on:pointermove={updateCoordinates}
/>

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