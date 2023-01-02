<script lang="ts">
  import { writable } from 'svelte/store';
  import { LayerCake, Svg, Html, Canvas } from 'layercake';
  import { config, devices } from '../lib/stores';
  import { scaleOrdinal, schemeCategory10 } from "d3";
  import { select } from "d3-selection";
  import { zoom, zoomIdentity } from 'd3-zoom';
  import { setContext } from 'svelte';
  import { Drawer, drawerStore } from '@skeletonlabs/skeleton';

  import type { Device } from '../lib/types';

  import Rooms from './Rooms.svelte';
  import Devices from './Devices.svelte';
  import FloorTabs from './FloorTabs.svelte';
  import Nodes from './Nodes.svelte';
  import AxisX from './AxisX.svelte';
  import AxisY from './AxisY.svelte';
  import DeviceDetails from './DeviceDetails.svelte';

  let svg:Element;
  let transform = zoomIdentity;
  const selected = writable<Device | null>();
  const hovered = writable<Device| null>();
  const floor = writable<number>(0);

  const handler = zoom()
    .scaleExtent([1, 40])
    .on("zoom", e => {transform = e.transform} );

  setContext('colors', scaleOrdinal(schemeCategory10))

  $: { if (svg) select(svg).call(handler) }
  $: bounds = $config?.floors[$floor]?.bounds

  function detail (d:Device | null) {
		$selected = d;
    drawerStore.open({id:"device"});
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

{#if bounds }
<div class="w-full h-full">
  <FloorTabs selected={floor} />
  <LayerCake x='0' y='1' flatData={ bounds } xReverse={ false } yReverse={ true } padding={ {top: 0, left: 0, bottom: 16, right: 0} }>
    <Svg bind:element={svg}>
      <AxisX {transform} />
      <AxisY {transform} />
      <Rooms {transform} floor={$floor} />
      <Nodes {transform} radarId={$hovered?.id ?? $selected?.id} floor={$floor} />
      <Devices {transform} floor={$floor} on:selected={ d => detail(d.detail) } on:hovered={ d => $hovered = d.detail } />
    </Svg>
  </LayerCake>
  <Drawer width="400px">
    {#if $drawerStore.id === 'device'}
     <DeviceDetails deviceId={$selected?.id} />
    {:else}
     <p>(fallback contents)</p>
    {/if}
  </Drawer>
</div>
{:else}
<div>Loading...</div>
{/if}
