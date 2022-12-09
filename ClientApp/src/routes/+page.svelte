<script lang="ts">
  import { writable } from 'svelte/store';
  import { LayerCake, Svg, Html, Canvas } from 'layercake';
  import { config, devices } from '../lib/stores';
  import type { Config, Device, Node, Room } from '../lib/types';

  import Rooms from './Rooms.svelte';
  import Devices from './Devices.svelte';
  import FloorTabs from './FloorTabs.svelte';
  import Nodes from './Nodes.svelte';
  import AxisX from './AxisX.svelte';
  import AxisY from './AxisY.svelte';

  const selected = writable<Device>();
  const hovered = writable<Device>();
  const floor = writable<number>(0);

  $: bounds = $config?.floors[$floor]?.bounds
</script>

<svelte:head>
	<title>ESPresense Companion</title>
</svelte:head>
{#if bounds }
<div class="w-full h-full">
  <FloorTabs selected={floor} />
  <LayerCake x='0' y='1' flatData={ bounds } xReverse={ false } yReverse={ true } padding={ {top: 0, left: 0, bottom: 72, right: 0} }>
    <Svg>
      <AxisX />
      <AxisY />
      <Rooms floor={$floor} />
      <Nodes radarId={$hovered?.id ?? $selected?.id} floor={$floor} />
      <Devices floor={$floor} on:selected={ r => $selected = r.detail } on:hovered={ r => $hovered = r.detail } />
    </Svg>
  </LayerCake>
</div>
{:else}
<div>Loading...</div>
{/if}
