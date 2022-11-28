<script>
  import { LayerCake, Svg, Html, Canvas } from 'layercake';
  import { config, devices, nodes } from '../lib/stores';
  import { writable } from 'svelte/store';

  import Rooms from './Rooms.svelte';
  import Devices from './Devices.svelte';
  import Nodes from './Nodes.svelte';
  import AxisX from './AxisX.svelte';
  import AxisY from './AxisY.svelte';

  export const radar = writable();
</script>

<svelte:head>
	<title>ESPresense Companion</title>
</svelte:head>

{#if $config?.bounds }
<div class="map">
  <LayerCake x='0' y='1' flatData={ $config.bounds } xReverse={ false } yReverse={ true } padding={ {top: 5, left: 5, bottom: 20, right: 5} }>
    <Svg>
      <AxisX />
      <AxisY />
      <Rooms />
      <Nodes radar={$radar} />
      <Devices on:selected={ r => $radar = r.detail } />
    </Svg>
  </LayerCake>
</div>
{:else}
<div>Loading...</div>
{/if}

<style>
  .map {
    position:absolute;
    top: 0;
    left: 0;
    width:100%;
    height:100%;
  }
</style>