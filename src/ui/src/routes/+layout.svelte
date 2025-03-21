<script lang="ts">
	import '../app.css';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import { Navigation, ToastProvider } from '@skeletonlabs/skeleton-svelte';
	import { computePosition, autoUpdate, offset, shift, flip, arrow } from '@floating-ui/dom';

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
	<ToastProvider />
	<AppShell>
		<svelte:fragment slot="sidebarLeft">
			<Navigation>
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
			</Navigation>
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