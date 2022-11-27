<script>
  import { LayerCake, Svg, Html, Canvas } from 'layercake';
  import { config, devices, nodes } from '../lib/stores';
  import Rooms from './Rooms.svelte';
  import Devices from './Devices.svelte';
  import Nodes from './Nodes.svelte';
</script>

<svelte:head>
	<title>ESPresense Companion</title>
</svelte:head>

{#if $config?.bounds }
<div class="map">
  <LayerCake x='0' y='1' flatData={ $config.bounds }>

    <Canvas>
      <Nodes />
    </Canvas>

    <Svg>
      <Devices />
      <Rooms />
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
