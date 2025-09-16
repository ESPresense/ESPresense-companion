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
	export let bounds: number[][] = [
		[0, 0],
		[500, 500]
	];

	let isDragging = false;
	// Offset (in screen space) from the pointer to the marker's center.
	let offset = { x: 0, y: 0 };

	// Helper function to get client coordinates from either mouse or touch event
	function getClientCoords(event: MouseEvent | TouchEvent) {
		if ('touches' in event) {
			return {
				clientX: event.touches[0].clientX,
				clientY: event.touches[0].clientY
			};
		} else {
			return {
				clientX: event.clientX,
				clientY: event.clientY
			};
		}
	}

	// When the user starts dragging...
	function handlePointerDown(event: MouseEvent | TouchEvent) {
		event.stopPropagation();
		isDragging = true;

		const coords = getClientCoords(event);

		// Compute the marker's current screen position.
		// The marker is rendered at: translate($xScale(position.x), $yScale(position.y))
		// and then the outer <g> applies the zoom transform.
		const markerScreenX = transform.k * $xScale(position.x) + transform.x;
		const markerScreenY = transform.k * $yScale(position.y) + transform.y;

		// Record the offset from the pointer to the marker center.
		offset.x = coords.clientX - markerScreenX;
		offset.y = coords.clientY - markerScreenY;

		event.preventDefault();

		// Add both mouse and touch event listeners
		window.addEventListener('mousemove', handlePointerMove, { capture: true });
		window.addEventListener('touchmove', handlePointerMove, { capture: true, passive: false });

		window.addEventListener('mouseup', handlePointerUp, { capture: true });
		window.addEventListener('touchend', handlePointerUp, { capture: true });
		window.addEventListener('touchcancel', handlePointerUp, { capture: true });
	}

	// While dragging...
	function handlePointerMove(event: MouseEvent | TouchEvent) {
		event.stopPropagation();
		if (!isDragging) return;

		const coords = getClientCoords(event);

		// Compute the new desired marker screen position,
		// maintaining the same offset from the pointer.
		const newMarkerScreenX = coords.clientX - offset.x;
		const newMarkerScreenY = coords.clientY - offset.y;

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

		// Prevent scrolling on touch devices
		if ('touches' in event) {
			event.preventDefault();
		}
	}

	// When the user stops dragging...
	function handlePointerUp(event: MouseEvent | TouchEvent) {
		event.stopPropagation();
		if (isDragging) {
			isDragging = false;

			// Remove both mouse and touch event listeners
			window.removeEventListener('mousemove', handlePointerMove, { capture: true });
			window.removeEventListener('touchmove', handlePointerMove, { capture: true });

			window.removeEventListener('mouseup', handlePointerUp, { capture: true });
			window.removeEventListener('touchend', handlePointerUp, { capture: true });
			window.removeEventListener('touchcancel', handlePointerUp, { capture: true });

			// Notify that dragging has completed
			dispatch('dragend', { position });
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
		<circle r="10" fill="none" stroke="#4CAF50" stroke-width="2" opacity="0.8" />
		<!-- Inner dot -->
		<circle r="3" fill="#4CAF50" opacity="0.8" />
		<!-- Invisible circle for easier interaction -->
		<circle class="no-zoom" r="15" role="button" tabindex="0" fill="transparent" onmousedowncapture={handlePointerDown} ontouchstartcapture={handlePointerDown} style="cursor: move" />
	</g>
</g>
