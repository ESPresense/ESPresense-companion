<script lang="ts">
  import '@skeletonlabs/skeleton/themes/theme-crimson.css';
  import '@skeletonlabs/skeleton/styles/all.css';
  import '../app.postcss';
  import { assets, base } from '$app/paths';
  import { AppShell, AppBar, AppRail, AppRailTile } from '@skeletonlabs/skeleton';
  import { writable, type Writable } from 'svelte/store';

  import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
  import map from '$lib/images/map.svg';
  import nodes from '$lib/images/nodes.svg';

  const storeValue: Writable<number> = writable(1);
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

        <AppRailTile label="Nodes" tag="a" href="{base}/nodes" value={2}>
          <img src={nodes} alt="Nodes" />
				</AppRailTile>

				<svelte:fragment slot="trail">
					<AppRailTile regionIcon="w-8" tag="a" href="https://github.com/ESPresense/ESPresense-companion" target="_blank" title="Trail">
            <img src={github} alt="GitHub" />
					</AppRailTile>
				</svelte:fragment>
    </AppRail>
   </svelte:fragment>
    <slot />
  </AppShell>
</div>


<style>
  img {
    fill: white;
  }
</style>
