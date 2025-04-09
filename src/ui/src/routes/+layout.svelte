<script lang="ts">
	import '../app.css';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import { Toaster } from '@skeletonlabs/skeleton-svelte';
	import { toaster } from '$lib/toaster';

	import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
	import map from '$lib/images/map.svg';
	import nodes from '$lib/images/nodes.svg';
	import devices from '$lib/images/devices.svg';
	import calibration from '$lib/images/calibration.svg';
	import cube from '$lib/images/cube.svg';

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

<div class="app h-full flex">
	<Toaster {toaster} />

	<!-- Sidebar -->
	<nav class="flex flex-col w-20 bg-gray-900 text-white space-y-4 py-4">
		<a href="https://espresense.com/companion" target="_blank" class="flex flex-col items-center space-y-1 hover:bg-gray-800 p-2">
			<img src={logo} class="px-2" alt="ESPresense Companion" />
		</a>

		{#each routes as route}
			<a href="{base}{route.href}" class="flex flex-col items-center space-y-1 hover:bg-gray-800 p-2 {current === `${base}${route.href}` ? 'bg-gray-800' : ''}">
				<img src={route.icon} class="px-2" alt={route.alt} />
				<span class="text-xs">{route.alt}</span>
			</a>
		{/each}

		<a href="https://github.com/ESPresense/ESPresense-companion" target="_blank" class="flex flex-col items-center space-y-1 hover:bg-gray-800 p-2 mt-auto">
			<img src={github} class="px-2" alt="GitHub" />
		</a>
	</nav>

	<!-- Main content -->
	<main class="flex-1 overflow-auto bg-slate-800">
		<slot />
	</main>
</div>

<style>
	img {
		fill: white;
	}
</style>