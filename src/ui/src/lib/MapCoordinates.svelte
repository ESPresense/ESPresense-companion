<script lang="ts">
	import { isEditableTarget } from './dom';
	import { zoomIdentity } from 'd3-zoom';
	import { getToastStore } from '#lib/toast/toastStore.js';
	import { getLayerCakeContext } from 'layercake';
	import type { MapScales } from '#lib/types.js';
	const toastStore = getToastStore();
	let { transform = zoomIdentity } = $props();
	// Plain state, not derived: the mouse handler owns these. As `$:` assignments they
	// were liable to be re-run and reset the readout to 0.
	let cursorX = $state(0);
	let cursorY = $state(0);

	let copiedCoords: string[] = [];
	let hasFocus = false;

	const chart = getLayerCakeContext<MapScales>();

	function updateCoordinates(event: MouseEvent) {
		if (!chart.xScale || !chart.yScale) return;

		const target = event.target as SVGElement;
		const svg = target.ownerSVGElement || (target as SVGSVGElement);

		if (!(svg instanceof SVGSVGElement)) return;

		try {
			const point = svg.createSVGPoint();
			point.x = event.clientX;
			point.y = event.clientY;

			const screenCTM = svg.getScreenCTM();
			if (!screenCTM) return;

			// Convert screen coordinates to SVG coordinates
			const svgPoint = point.matrixTransform(screenCTM.inverse());

			// layercake 11 insets the <svg> itself by the padding (rather than translating a <g> inside it),
			// so svg coordinates are already layer coordinates; subtracting the padding again shifts the readout.
			const transformedX = (svgPoint.x - transform.x) / transform.k;
			const transformedY = (svgPoint.y - transform.y) / transform.k;

			cursorX = chart.xScale.invert(transformedX);
			cursorY = chart.yScale.invert(transformedY);
		} catch (error) {
			console.error('Error updating coordinates:', error);
		}
	}

	function handleKeydown(event: KeyboardEvent) {
		if (isEditableTarget(event.target)) return;
		// Check for Ctrl/Cmd + C
		if ((event.ctrlKey || event.metaKey) && event.key === 'c') {
			event.preventDefault();
			hasFocus = true;
			const roundedX = Math.round(cursorX * 10) / 10;
			const roundedY = Math.round(cursorY * 10) / 10;
			const coords = `          - [${roundedX},${roundedY}]`;
			copiedCoords = [...copiedCoords, coords];
			navigator.clipboard
				.writeText(copiedCoords.join('\n'))
				.then(() => {
					toastStore.trigger({
						message: `Copied ${copiedCoords.length} coordinate${copiedCoords.length > 1 ? 's' : ''} to clipboard!`,
						background: 'preset-filled-success-500',
						timeout: 1000
					});
				})
				.catch((error) => {
					toastStore.trigger({
						message: `Failed to copy to clipboard: ${error}`,
						background: 'preset-filled-error-500'
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
				background: 'preset-filled-primary-500',
				timeout: 1000
			});
		}
	}
</script>

<svelte:window onmousemove={updateCoordinates} onkeydown={handleKeydown} onblur={handleFocusOut} />

<g transform="translate({chart.width - 120}, {chart.height - 40})">
	<rect width="110" height="30" rx="4" fill="#2563eb" class="shadow-lg" />
	<text x="55" y="19" text-anchor="middle" fill="white" font-size="12">
		X: {Math.round(cursorX * 10) / 10}, Y: {Math.round(cursorY * 10) / 10}
	</text>
</g>
