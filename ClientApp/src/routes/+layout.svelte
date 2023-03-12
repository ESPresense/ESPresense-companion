<script lang="ts">
  import '@skeletonlabs/skeleton/themes/theme-crimson.css';
  import '@skeletonlabs/skeleton/styles/all.css';
  import "../app.css";
  import { assets, base } from '$app/paths';
  import { AppShell, AppBar, AppRail, AppRailTile } from '@skeletonlabs/skeleton';
  import { writable, type Writable } from 'svelte/store';
  import { Drawer, drawerStore } from '@skeletonlabs/skeleton';
  import { Toast, toastStore } from '@skeletonlabs/skeleton';
  import { selected } from '../lib/stores';
  import { beforeNavigate } from '$app/navigation';
  import { updated } from '$app/stores';

  import type { Device } from '../lib/types';

  import logo from '$lib/images/logo.svg';
  import github from '$lib/images/github.svg';
  import map from '$lib/images/map.svg';
  import nodes from '$lib/images/nodes.svg';
  import devices from '$lib/images/devices.svg';
  import DeviceDetails from './DeviceDetails.svelte';

  const storeValue: Writable<number> = writable(1);

  beforeNavigate(({ willUnload, to }) => {
    if ($updated && !willUnload && to?.url) {
      location.href = to.url.href;
    }
  });
</script>

<div class="app h-full">
  <AppShell>
    <svelte:fragment slot="sidebarLeft">
      <AppRail selected={storeValue} width="w-16">

        <svelte:fragment slot="lead">
          <AppRailTile tag="a" href="https://espresense.com/companion" target="_blank" >
            <img src={logo} alt="ESPresense Companion"/>
          </AppRailTile>
        </svelte:fragment>

        <AppRailTile label="Map" tag="a" href="{base}/" value={1}>
          <img src={map} alt="Map" />
        </AppRailTile>

        <AppRailTile label="Devices" tag="a" href="{base}/devices" value={2}>
          <img src={devices} alt="Devices" />
        </AppRailTile>

        <AppRailTile label="Nodes" tag="a" href="{base}/nodes" value={3}>
          <img src={nodes} alt="Nodes" />
        </AppRailTile>

        <svelte:fragment slot="trail">
          <AppRailTile regionIcon="w-8" tag="a" href="https://github.com/ESPresense/ESPresense-companion" target="_blank" title="Trail">
            <img src={github} alt="GitHub" />
          </AppRailTile>
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
