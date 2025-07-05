<script lang="ts">
	import '../app.css';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import { Navigation, ToastProvider } from '@skeletonlabs/skeleton-svelte';
	import { computePosition, autoUpdate, offset, shift, flip, arrow } from '@floating-ui/dom';
	import { Modal } from '@skeletonlabs/skeleton-svelte';

	import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
	import map from '$lib/images/map.svg';
	import nodes from '$lib/images/nodes.svg';
	import devices from '$lib/images/devices.svg';
	import calibration from '$lib/images/calibration.svg';
	import cube from '$lib/images/cube.svg';

	// Skeleton v3 no longer requires initializeStores or storePopup configuration


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
	<ToastProvider />
	<div class="flex h-full">
		<!-- Sidebar -->
		<div class="w-20 bg-surface-800 flex flex-col items-center py-4 space-y-4">
			<!-- Logo -->
			<a href="https://espresense.com/companion" target="_blank" class="p-2">
				<img src={logo} class="w-12 h-12" alt="ESPresense Companion" />
			</a>
			
			<!-- Navigation Links -->
			{#each routes as route}
				<a
					href="{base}{route.href}"
					class="p-2 rounded-lg transition-colors {current === `${base}${route.href}` ? 'bg-primary-500 text-white' : 'hover:bg-surface-700'}"
					title={route.alt}
				>
					<img src={route.icon} class="w-8 h-8" alt={route.alt} />
				</a>
			{/each}
			
			<!-- GitHub Link -->
			<a href="https://github.com/ESPresense/ESPresense-companion" target="_blank" class="p-2 mt-auto hover:bg-surface-700 rounded-lg">
				<img src={github} class="w-6 h-6" alt="GitHub" />
			</a>
		</div>
		
		<!-- Main Content -->
		<div class="flex-1 overflow-hidden">
			<slot />
		</div>
	</div>
</div>

<style>
	img {
		fill: white;
	}
</style>