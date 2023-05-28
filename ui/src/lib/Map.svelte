<script lang="ts">
	import { writable } from 'svelte/store';
	import { LayerCake, Svg, Html, Canvas } from 'layercake';
	import { config, devices } from '../lib/stores';
	import { scaleOrdinal, schemeCategory10 } from 'd3';
	import { select } from 'd3-selection';
	import { zoom, zoomIdentity } from 'd3-zoom';
	import { setContext } from 'svelte';

	import type { Device } from '../lib/types';

	import Rooms from './Rooms.svelte';
	import Devices from './Devices.svelte';
	import Nodes from './Nodes.svelte';
	import AxisX from './AxisX.svelte';
	import AxisY from './AxisY.svelte';

	let svg: Element;
	let transform = zoomIdentity;
	const hovered = writable<Device | null>();

	export let floorId: string | null = null;
	export let deviceId: string | null = null;

	$: device = $devices.find((d) => d.id === deviceId);
	$: floor = $config?.floors.find((f) => f.id === floorId) ?? $config?.floors.find((f) => f != null);
	$: bounds = floor?.bounds;

	const handler = zoom()
		.scaleExtent([1, 40])
		.on('zoom', (e) => {
			transform = e.transform;
		});

  setContext('colors', scaleOrdinal(schemeCategory10))

  $: { if (svg) select(svg).call(handler) }

</script>

{#if bounds}
  <LayerCake x='0' y='1' flatData={ bounds } xReverse={ false } yReverse={ true } padding={ {top: 16, left: 16, bottom: 16, right: 16} }>
		<Svg bind:element={svg}>
			<AxisX {transform} />
			<AxisY {transform} />
			<Rooms {transform} {floorId} />
			<Nodes {transform} {floorId} radarId={$hovered?.id ?? device?.id} />
      <Devices {transform} { floorId } {deviceId} on:selected on:hovered={ d => $hovered = d.detail } />
		</Svg>
	</LayerCake>
{:else}
	<div>Loading...</div>
{/if}
