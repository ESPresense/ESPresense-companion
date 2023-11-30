<script lang="ts">
	import '../app.postcss';

	import { computePosition, autoUpdate, flip, shift, offset, arrow } from '@floating-ui/dom';
	import { base } from '$app/paths';
	import { AppShell, AppRail, AppRailAnchor, Drawer, Toast, Modal, initializeStores, storePopup } from '@skeletonlabs/skeleton';
	import { beforeNavigate } from '$app/navigation';
	import { updated } from '$app/stores';
	import { history } from '$lib/stores';

	import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
	import map from '$lib/images/map.svg';
	import nodes from '$lib/images/nodes.svg';
	import devices from '$lib/images/devices.svg';
	import calibration from '$lib/images/calibration.svg';

	initializeStores();

	storePopup.set({ computePosition, autoUpdate, offset, shift, flip, arrow });

	beforeNavigate(({ willUnload, to }) => {
		var next = to?.url?.pathname;
		if (next) $history = [next, ...$history];
		if ($updated && !willUnload && to?.url) {
			location.href = to.url.href;
		}
	});

	$: current = $history[0];
	$: console.log('Current: ', current);
</script>

<div class="app h-full">
  <Modal />
  <Toast />
	<AppShell>
		<svelte:fragment slot="sidebarLeft">
			<AppRail>
				<svelte:fragment slot="lead">
					<AppRailAnchor href="https://espresense.com/companion" target="_blank" group="main">
						<img src={logo} class="px-6" alt="ESPresense Companion" />
					</AppRailAnchor>
				</svelte:fragment>

				<AppRailAnchor href="{base}/" name="map" selected={current == `${base}/`}>
					<img src={map} class="px-6" alt="Map" />
					<span>Map</span>
				</AppRailAnchor>

				<AppRailAnchor href="{base}/devices" name="devices" selected={current == `${base}/devices`}>
					<img src={devices} class="px-6" alt="Devices" />
					<span>Devices</span>
				</AppRailAnchor>

				<AppRailAnchor href="{base}/nodes" name="nodes" selected={current == `${base}/nodes`}>
					<img src={nodes} class="px-6" alt="Nodes" />
					<span>Nodes</span>
				</AppRailAnchor>

				<AppRailAnchor href="{base}/calibration" name="calibration" selected={current == `${base}/calibration`}>
					<img src={calibration} class="px-4" alt="Calibration" />
					<span>Calibration</span>
				</AppRailAnchor>

				<svelte:fragment slot="trail">
					<AppRailAnchor regionIcon="w-8" href="https://github.com/ESPresense/ESPresense-companion" target="_blank">
						<img src={github} class="px-4" alt="GitHub" />
					</AppRailAnchor>
				</svelte:fragment>
			</AppRail>
		</svelte:fragment>
		<slot />
		<Drawer width="400px" />
	</AppShell>
</div>

<style>
	img {
		fill: white;
	}
</style>
