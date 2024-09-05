<script lang="ts">
	import { zoomIdentity, type ZoomScale } from 'd3-zoom';
	import { createEventDispatcher, getContext } from 'svelte';
	import type { Readable } from 'svelte/store';

	const dispatch = createEventDispatcher();

	// Get the scales from the LayerCake context.
	const context: { xScale: Readable<ZoomScale>; yScale: Readable<ZoomScale> } = getContext('LayerCake');
	const { xScale, yScale } = context;

	// The current d3 zoom transform.
	export let transform = zoomIdentity;
	// The calibration spot's logical (data) position.
	export let position = { x: 0, y: 0 };
	// Data-space bounds.
	export let bounds: number[][] = [[0, 0], [500, 500]];

	let isDragging = false;
	// Offset (in screen space) from the pointer to the marker's center.
	let offset = { x: 0, y: 0 };

	// When the user starts dragging...
	function handleMouseDown(event: MouseEvent) {
		event.stopPropagation();
		isDragging = true;

		// Compute the marker's current screen position.
		// The marker is rendered at: translate($xScale(position.x), $yScale(position.y))
		// and then the outer <g> applies the zoom transform.
		const markerScreenX = transform.k * $xScale(position.x) + transform.x;
		const markerScreenY = transform.k * $yScale(position.y) + transform.y;

		// Record the offset from the pointer to the marker center.
		offset.x = event.clientX - markerScreenX;
		offset.y = event.clientY - markerScreenY;

		event.preventDefault();
		window.addEventListener('mousemove', handleMouseMove, { capture: true });
		window.addEventListener('mouseup', handleMouseUp, { capture: true });
	}

	// While dragging...
	function handleMouseMove(event: MouseEvent) {
		event.stopPropagation();
		if (!isDragging) return;

		// Compute the new desired marker screen position,
		// maintaining the same offset from the pointer.
		const newMarkerScreenX = event.clientX - offset.x;
		const newMarkerScreenY = event.clientY - offset.y;

		// Convert the screen coordinates back into data coordinates.
		// First, remove the zoom transform: subtract translation and divide by scale.
		// Then, invert the scales to get back to data space.
		const newDataX = $xScale.invert((newMarkerScreenX - transform.x) / transform.k);
		const newDataY = $yScale.invert((newMarkerScreenY - transform.y) / transform.k);

		// Optionally, clamp to bounds.
		const clampedX = Math.max(bounds[0][0], Math.min(bounds[1][0], newDataX));
		const clampedY = Math.max(bounds[0][1], Math.min(bounds[1][1], newDataY));

		// Update position - this will trigger the two-way binding update
		position = { x: clampedX, y: clampedY };
	}

	// When the user stops dragging...
	function handleMouseUp(event: MouseEvent) {
		event.stopPropagation();
		if (isDragging) {
			isDragging = false;
			window.removeEventListener('mousemove', handleMouseMove, { capture: true });
			window.removeEventListener('mouseup', handleMouseUp, { capture: true });
		}
	}
</script>

<!--
  The outer <g> applies the d3‑zoom transform.
  The inner <g> positions the calibration spot using $xScale and $yScale.
-->
<g transform={transform.toString()}>
	<g transform="translate({$xScale(position.x)}, {$yScale(position.y)})" style="cursor: move">
		<!-- Outer ring -->
		<circle
			r="10"
			fill="none"
			stroke="#4CAF50"
			stroke-width="2"
			opacity="0.8"
		/>
		<!-- Inner dot -->
		<circle
			r="3"
			fill="#4CAF50"
			opacity="0.8"
		/>
		<!-- Invisible circle for easier interaction -->
		<circle
			class="no-zoom"
			r="15"
			role="button"
			tabindex="0"
			fill="transparent"
			on:mousedown|capture={handleMouseDown}
			style="cursor: move"
		/>
	</g>
</g>