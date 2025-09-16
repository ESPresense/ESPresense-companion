<script lang="ts">
	import { LayerCake, Svg } from 'layercake';
	import { config } from '$lib/stores';
	import { scaleOrdinal, schemeCategory10 } from 'd3';
	import { select } from 'd3-selection';
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

	let svg: Element;
	let transform = zoomIdentity;

	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let nodeId: string | null = null;
	export let exclusive: boolean = false;
	export let calibrate: boolean = false;
	export let calibrationSpot: { x: number; y: number } | null = null;
	export let onselected: ((item: Device | Node) => void) | undefined = undefined;

	$: floor = $config?.floors.find((f) => f.id === floorId) ?? $config?.floors.find((f) => f != null);
	$: bounds = floor?.bounds;
	$: squareBounds = bounds ? makeSquareBounds(bounds) : undefined;

	function makeSquareBounds(bounds: number[][]): number[][] {
		const maxDim = Math.max(bounds[1][0], bounds[1][1]);
		return [
			[bounds[0][0], bounds[0][1], bounds[0][2]],
			[maxDim, maxDim, bounds[1][2]]
		];
	}

	const handler = zoom()
		.scaleExtent([0.5, 40])
		.wheelDelta((event) => {
			// Only zoom if shift key is pressed
			if (event.shiftKey) {
				return -(event.deltaY + event.deltaX * 0.25) * 0.002;
			}
			// Return 0 to prevent zooming when shift key is not pressed
			return 0;
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

		if (newTransform !== transform) {
			select(svg).call(handler.transform, newTransform);
		}
	}

	setContext('colors', scaleOrdinal(schemeCategory10));

	$: {
		if (svg) select(svg).call(handler);
	}

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
