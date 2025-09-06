<script lang="ts">
	import '../app.postcss';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import { AppShell, AppRail, AppRailAnchor, Drawer, initializeStores, storePopup } from '@skeletonlabs/skeleton';
	import { computePosition, autoUpdate, offset, shift, flip, arrow } from '@floating-ui/dom';
	import Modal from '$lib/modal/Modal.svelte';
	import Toast from '$lib/toast/Toast.svelte';

	import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
	import map from '$lib/images/map.svg';
	import nodes from '$lib/images/nodes.svg';
	import devices from '$lib/images/devices.svg';
	import calibration from '$lib/images/calibration.svg';
	import cube from '$lib/images/cube.svg';

	initializeStores();

	storePopup.set({ computePosition, autoUpdate, offset, shift, flip, arrow });


	$: current = $page.url.pathname;

	const routes = [
		{ href: '/', name: 'map', icon: map, alt: 'Map' },
		{ href: '/3d', name: '3d', icon: cube, alt: '3D View' },
		{ href: '/devices', name: 'devices', icon: devices, alt: 'Devices' },
		{ href: '/nodes', name: 'nodes', icon: nodes, alt: 'Nodes' },
		{ href: '/calibration', name: 'calibration', icon: calibration, alt: 'Calibration' },
		{ href: '/geolocation', name: 'geolocation', icon: map, alt: 'Geolocation' },
	];
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

				{#each routes as route}
					<AppRailAnchor
						href="{base}{route.href}"
						name={route.name}
						selected={current === `${base}${route.href}`}
					>
						<img src={route.icon} class="px-6" alt={route.alt} />
						<span>{route.alt}</span>
					</AppRailAnchor>
				{/each}

				<svelte:fragment slot="trail">
					<AppRailAnchor href="https://github.com/ESPresense/ESPresense-companion" target="_blank">
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
