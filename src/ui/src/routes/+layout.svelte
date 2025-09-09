<script lang="ts">
	import '../app.css';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import Modal from '$lib/modal/Modal.svelte';
	import Toast from '$lib/toast/Toast.svelte';

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
		{ href: '/geolocation', name: 'geolocation', icon: map, alt: 'Geolocation' }
	];
</script>

<div class="app h-full">
	<Modal />
	<Toast />
	<div class="flex h-full bg-surface-50-900-token">
		<!-- App Rail Sidebar -->
		<div class="flex w-16 flex-col bg-surface-100-800-token border-r border-surface-300-600-token">
			<!-- Logo Section -->
			<div class="flex flex-col items-center py-4">
				<a href="https://espresense.com/companion" target="_blank" class="flex items-center justify-center w-12 h-12 rounded-lg hover:bg-surface-200-700-token transition-colors duration-200" title="ESPresense Companion">
					<img src={logo} class="w-8 h-8" alt="ESPresense Companion" />
				</a>
			</div>

			<!-- Navigation Rail -->
			<nav class="flex flex-col flex-1 items-center space-y-2 px-2">
				{#each routes as route}
					<a href="{base}{route.href}" class="flex flex-col items-center justify-center w-12 h-12 rounded-lg transition-all duration-200 group relative {current === `${base}${route.href}` ? 'bg-primary-500 text-white shadow-lg' : 'text-surface-600-300-token hover:bg-surface-200-700-token hover:text-surface-900-50-token'}" title={route.alt}>
						<img src={route.icon} class="w-6 h-6 transition-transform group-hover:scale-110" alt={route.alt} />

						<!-- Active indicator -->
						{#if current === `${base}${route.href}`}
							<div class="absolute -right-2 top-1/2 transform -translate-y-1/2 w-1 h-6 bg-primary-500 rounded-l-full"></div>
						{/if}

						<!-- Tooltip on hover -->
						<div class="absolute left-full ml-2 px-2 py-1 bg-surface-900-50-token text-surface-50-900-token text-sm rounded opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity duration-200 whitespace-nowrap z-10">
							{route.alt}
						</div>
					</a>
				{/each}
			</nav>

			<!-- Footer Section -->
			<div class="flex flex-col items-center pb-4">
				<a href="https://github.com/ESPresense/ESPresense-companion" target="_blank" class="flex items-center justify-center w-12 h-12 rounded-lg hover:bg-surface-200-700-token transition-colors duration-200 text-surface-600-300-token hover:text-surface-900-50-token group" title="View on GitHub">
					<img src={github} class="w-5 h-5 transition-transform group-hover:scale-110" alt="GitHub" />
				</a>
			</div>
		</div>

		<!-- Main Content Area -->
		<main class="flex-1 overflow-hidden">
			<slot />
		</main>
	</div>
</div>

<style>
	img {
		fill: white;
	}
</style>
