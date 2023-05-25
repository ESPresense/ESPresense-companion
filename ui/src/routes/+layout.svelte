<script lang="ts">
	// The ordering of these imports is critical to your app working properly
	import '@skeletonlabs/skeleton/themes/theme-crimson.css';
	// If you have source.organizeImports set to true in VSCode, then it will auto change this ordering
	import '@skeletonlabs/skeleton/styles/skeleton.css';
	// Most of your app wide CSS should be put in this file
	import '../app.postcss';

  import { computePosition, autoUpdate, flip, shift, offset, arrow } from '@floating-ui/dom';
  import { storePopup } from '@skeletonlabs/skeleton';

  import { assets, base } from '$app/paths';
  import { AppShell, AppBar, AppRail, AppRailTile, AppRailAnchor } from '@skeletonlabs/skeleton';
  import { Drawer, drawerStore } from '@skeletonlabs/skeleton';
  import { Toast, toastStore } from '@skeletonlabs/skeleton';
  import { selected } from '../lib/stores';
  import { beforeNavigate } from '$app/navigation';
  import { updated } from '$app/stores';

  import logo from '$lib/images/logo.svg';
  import github from '$lib/images/github.svg';
  import map from '$lib/images/map.svg';
  import nodes from '$lib/images/nodes.svg';
  import devices from '$lib/images/devices.svg';
  import DeviceDetails from './DeviceDetails.svelte';

  storePopup.set({ computePosition, autoUpdate, flip, shift, offset, arrow });

  beforeNavigate(({ willUnload, to }) => {
    if ($updated && !willUnload && to?.url) {
      location.href = to.url.href;
    }
  });

	let currentTile: number = 0;
</script>

<div class="app h-full">
  <AppShell>
    <svelte:fragment slot="sidebarLeft">
      <AppRail width="w-16">

        <svelte:fragment slot="lead">
          <AppRailAnchor href="https://espresense.com/companion" target="_blank" group="main" >
            <img src={logo} class="px-4" alt="ESPresense Companion"/>
          </AppRailAnchor>
        </svelte:fragment>

        <AppRailAnchor href="{base}/" value={1} name="map" bind:group={currentTile} >
          <svelte:fragment slot="lead"><img src={map} class="px-4" alt="Map" /></svelte:fragment>
          <span>Map</span>
        </AppRailAnchor>

        <AppRailAnchor href="{base}/devices" name="devices" value={2} bind:group={currentTile} >
          <svelte:fragment slot="lead"><img src={devices} class="px-4" alt="Devices" /></svelte:fragment>
          <span>Devices</span>
        </AppRailAnchor>

        <AppRailAnchor href="{base}/nodes" value={3} name="nodes" bind:group={currentTile} >
          <svelte:fragment slot="lead"><img src={nodes} class="px-4" alt="Nodes" /></svelte:fragment>
          <span>Nodes</span>
        </AppRailAnchor>

        <AppRailAnchor href="{base}/calibration" value={4} name="calibration" bind:group={currentTile} >
          <svelte:fragment slot="lead"><img src={nodes} class="px-4" alt="Calibration" /></svelte:fragment>
          <span>Calibration</span>
        </AppRailAnchor>

        <svelte:fragment slot="trail">
          <AppRailAnchor regionIcon="w-8" href="https://github.com/ESPresense/ESPresense-companion" target="_blank">
            <img src={github} class="px-4" alt="GitHub" />
          </AppRailAnchor>
        </svelte:fragment>
    </AppRail>
   </svelte:fragment>
    <Toast />
    <slot />
    <Drawer width="400px">
      {#if $drawerStore.id === 'device'}
       <DeviceDetails deviceId={$selected?.id} />
      {:else}
       <p>(fallback contents)</p>
      {/if}
    </Drawer>
  </AppShell>
</div>

<style>
  img {
    fill: white;
  }
</style>
