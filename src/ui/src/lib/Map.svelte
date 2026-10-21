<script lang="ts">
	import { isEditableTarget } from './dom';
	import { LayerCake, Svg } from 'layercake';
	import { config } from '$lib/stores';
	import { scaleOrdinal, schemeCategory10 } from 'd3';
	import { pointer, select } from 'd3-selection';
	import { zoom, zoomIdentity } from 'd3-zoom';
	import { setContext } from 'svelte';
	import type { Device, Node } from '$lib/types';

	import Rooms from './Rooms.svelte';
	import Devices from './Devices.svelte';
	import Nodes from './Nodes.svelte';
	import AxisX from './AxisX.svelte';
	import AxisY from './AxisY.svelte';
	import MapCoordinates from './MapCoordinates.svelte';
	import CalibrationSpot from './CalibrationSpot.svelte';

	let svg: SVGElement | undefined = $state();
	let transform = $state(zoomIdentity);

	interface Props {
		floorId?: string | null;
		deviceId?: string | null;
		nodeId?: string | null;
		exclusive?: boolean;
		calibrate?: boolean;
		calibrationSpot?: { x: number; y: number } | null;
		onselected?: ((item: Device | Node) => void) | undefined;
	}

	let { floorId = $bindable(null), deviceId = $bindable(null), nodeId = $bindable(null), exclusive = false, calibrate = false, calibrationSpot = $bindable(null), onselected = undefined }: Props = $props();

	function makeSquareBounds(bounds: number[][]): number[][] {
		const maxDim = Math.max(bounds[1][0], bounds[1][1]);
		return [
			[bounds[0][0], bounds[0][1], bounds[0][2]],
			[maxDim, maxDim, bounds[1][2]]
		];
	}

	const handler = zoom<SVGElement, unknown>()
		.scaleExtent([0.5, 40])
		// Shift+drag is box zoom (handled below), so it must not also start a pan.
		.filter((event) => !(event.shiftKey && event.type === 'mousedown') && (!event.ctrlKey || event.type === 'wheel') && !event.button)
		.wheelDelta((event) => {
			// Like Google Maps: scroll and pinch both zoom at the cursor. Trackpad pinch arrives as a
			// ctrlKey wheel event with small deltas, so it gets a larger multiplier (and handling it
			// here stops the browser from zooming the whole page).
			const unit = event.deltaMode === 1 ? 0.05 : event.deltaMode ? 1 : 0.002;
			return -event.deltaY * unit * (event.ctrlKey ? 5 : 1);
		})
		.on('zoom', (e) => {
			transform = e.transform;
		});

	function hoveredDevice(device: Device | null) {
		if (exclusive) return;
		deviceId = device?.id ?? null;
	}

	function hoveredNode(node: Node | null) {
		if (exclusive) return;
		nodeId = node?.id ?? null;
	}

	function selectedDevice(device: Device) {
		onselected?.(device);
	}

	function selectedNode(node: Node) {
		onselected?.(node);
	}

	function handleKeyboard(event: KeyboardEvent) {
		if (isEditableTarget(event.target)) return;
		const zoomFactor = event.shiftKey ? 1.005 : 1.1;
		const translateAmount = event.shiftKey ? 1 : 50;
		let newTransform = transform;

		switch (event.key) {
			case '0':
				event.preventDefault();
				newTransform = zoomIdentity.translate(transform.x, transform.y);
				break;
			case '=':
			case '+':
				event.preventDefault();
				newTransform = transform.scale(zoomFactor);
				break;
			case '-':
			case '_':
				event.preventDefault();
				newTransform = transform.scale(1 / zoomFactor);
				break;
			case 'ArrowLeft':
				event.preventDefault();
				newTransform = transform.translate(-translateAmount / transform.k, 0);
				break;
			case 'ArrowRight':
				event.preventDefault();
				newTransform = transform.translate(translateAmount / transform.k, 0);
				break;
			case 'ArrowUp':
				event.preventDefault();
				newTransform = transform.translate(0, -translateAmount / transform.k);
				break;
			case 'ArrowDown':
				event.preventDefault();
				newTransform = transform.translate(0, translateAmount / transform.k);
				break;
		}

		if (svg && newTransform !== transform) {
			select(svg).call(handler.transform, newTransform);
		}
	}

	setContext('colors', scaleOrdinal(schemeCategory10));

	function getXRange({ height, width }: { height: number; width: number }) {
		const min = 0;
		const max = Math.min(height, width);
		return $config?.map?.flipX ? [max, min] : [min, max];
	}

	function getYRange({ height, width }: { height: number; width: number }) {
		const min = 0;
		const max = Math.min(height, width);
		return $config?.map?.flipY ? [max, min] : [min, max];
	}
	let floor = $derived($config?.floors.find((f) => f.id === floorId) ?? $config?.floors.find((f) => f != null));
	let bounds = $derived(floor?.bounds);
	let squareBounds = $derived(bounds ? makeSquareBounds(bounds) : undefined);
	$effect(() => {
		if (svg) select(svg).call(handler);
	});

	// Shift+drag draws a box and zooms to it, like Google Maps.
	$effect(() => {
		const el = svg;
		if (!el) return;

		let start: [number, number] | null = null;
		let box: ReturnType<typeof select<SVGRectElement, unknown>> | null = null;

		const onDown = (event: MouseEvent) => {
			if (!event.shiftKey || event.button !== 0) return;
			event.preventDefault();
			start = pointer(event, el);
			box = select(el).append('rect').attr('fill', 'rgba(59,130,246,0.15)').attr('stroke', 'rgb(59,130,246)').attr('stroke-dasharray', '4 2').attr('pointer-events', 'none').attr('width', 0).attr('height', 0);
			window.addEventListener('mousemove', onMove);
			window.addEventListener('mouseup', onUp);
		};

		const onMove = (event: MouseEvent) => {
			if (!start || !box) return;
			const [x, y] = pointer(event, el);
			box
				.attr('x', Math.min(start[0], x))
				.attr('y', Math.min(start[1], y))
				.attr('width', Math.abs(x - start[0]))
				.attr('height', Math.abs(y - start[1]));
		};

		const onUp = (event: MouseEvent) => {
			window.removeEventListener('mousemove', onMove);
			window.removeEventListener('mouseup', onUp);
			box?.remove();
			box = null;
			if (!start) return;
			const [x, y] = pointer(event, el);
			const [x0, y0] = start;
			start = null;
			const bw = Math.abs(x - x0);
			const bh = Math.abs(y - y0);
			if (bw < 5 || bh < 5) return; // a shift-click, not a drag

			const { width, height } = el.getBoundingClientRect();
			const k = Math.min(40, Math.max(0.5, transform.k * Math.min(width / bw, height / bh)));
			const [cx, cy] = transform.invert([(x + x0) / 2, (y + y0) / 2]);
			const target = zoomIdentity
				.translate(width / 2, height / 2)
				.scale(k)
				.translate(-cx, -cy);
			select(el).call(handler.transform, target);
		};

		el.addEventListener('mousedown', onDown);
		return () => {
			el.removeEventListener('mousedown', onDown);
			window.removeEventListener('mousemove', onMove);
			window.removeEventListener('mouseup', onUp);
			box?.remove();
		};
	});
</script>

<svelte:window onkeydown={handleKeyboard} />

{#if bounds}
	<LayerCake x="0" y="1" xRange={getXRange} yRange={getYRange} flatData={squareBounds} padding={{ top: 16, left: 16, bottom: 16, right: 16 }}>
		<Svg bind:element={svg}>
			<MapCoordinates {transform} />
			<AxisX {transform} />
			<AxisY {transform} />
			<Rooms {transform} {floorId} />
			<Nodes {transform} {floorId} {deviceId} {nodeId} onselected={selectedNode} onhovered={hoveredNode} />
			<Devices {transform} {floorId} {deviceId} {exclusive} onselected={selectedDevice} onhovered={hoveredDevice} />
			{#if calibrate && calibrationSpot}
				<CalibrationSpot {transform} {bounds} bind:position={calibrationSpot} />
			{/if}
		</Svg>
	</LayerCake>
{:else}
	<div>Loading...</div>
{/if}
