<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import type { LayerCakeContext } from '$lib/types';
	import { getToastStore } from '@skeletonlabs/skeleton';

	const toastStore = getToastStore();
	export let transform = zoomIdentity;
	$: cursorX = 0;
	$: cursorY = 0;

	let copiedCoords: string[] = [];
	let hasFocus = false;

	const { xScale, yScale, width, height, padding } = getContext<any>('LayerCake');

	function updateCoordinates(event: MouseEvent) {
		if (!$xScale || !$yScale) return;

		const target = event.target as SVGElement;
		const svg = target.ownerSVGElement || (target as SVGSVGElement);
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

	function handleKeydown(event: KeyboardEvent) {
		// Check for Ctrl/Cmd + C
		if ((event.ctrlKey || event.metaKey) && event.key === 'c') {
			event.preventDefault();
			hasFocus = true;
			const coords = `      - [${cursorX.toFixed(2)},${cursorY.toFixed(2)}]`;
			copiedCoords = [...copiedCoords, coords];
			navigator.clipboard
				.writeText(copiedCoords.join('\n'))
				.then(() => {
					toastStore.trigger({
						message: `Copied ${copiedCoords.length} coordinate${copiedCoords.length > 1 ? 's' : ''} to clipboard!`,
						background: 'variant-filled-success'
					});
				})
				.catch((error) => {
					toastStore.trigger({
						message: `Failed to copy to clipboard: ${error}`,
						background: 'variant-filled-error'
					});
				});
		}
	}

	function handleFocusOut() {
		if (hasFocus) {
			hasFocus = false;
			copiedCoords = [];
			toastStore.trigger({
				message: 'Coordinate collection reset',
				background: 'variant-filled-primary'
			});
		}
	}
</script>

<svelte:window on:mousemove={updateCoordinates} on:keydown={handleKeydown} on:blur={handleFocusOut} />

<g transform="translate({$width - 120}, {$height - 40})">
	<rect width="110" height="30" rx="4" fill="#2563eb" class="shadow-lg" />
	<text x="55" y="19" text-anchor="middle" fill="white" font-size="12">
		X: {cursorX.toFixed(2)}, Y: {cursorY.toFixed(2)}
	</text>
</g>
