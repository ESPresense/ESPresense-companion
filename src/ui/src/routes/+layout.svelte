<script lang="ts">
	import '../app.css';
	import { base } from '$app/paths';
	import { page } from '$app/stores';
	import { Navigation, Toaster, createToaster } from '@skeletonlabs/skeleton-svelte';
	import { setContext } from 'svelte';
	
	let { children } = $props();

	import { computePosition, autoUpdate, offset, shift, flip, arrow } from '@floating-ui/dom';

	import { Modal } from '@skeletonlabs/skeleton-svelte';
	import logo from '$lib/images/logo.svg';
	import github from '$lib/images/github.svg';
	import map from '$lib/images/map.svg';
	import nodes from '$lib/images/nodes.svg';
	import devices from '$lib/images/devices.svg';
	import calibration from '$lib/images/calibration.svg';
	import cube from '$lib/images/cube.svg';

	let current = $derived($page.url.pathname);

	let toaster = $state(createToaster());
	
	// Set the toaster in context so it can be accessed by the utility function
	setContext('toast', toaster);

	const routes = [
		{
			href: '/',
			name: 'map',
			icon: map,
			alt: 'Map'
		},
		{
			href: '/3d',
			name: '3d',
			icon: cube,
			alt: '3D View'
		},
		{
			href: '/devices',
			name: 'devices',
			icon: devices,
			alt: 'Devices'
		},
		{
			href: '/nodes',
			name: 'nodes',
			icon: nodes,
			alt: 'Nodes'
		},
		{
			href: '/calibration',
			name: 'calibration',
			icon: calibration,
			alt: 'Calibration'
		},
		{
			href: '/geolocation',
			name: 'geolocation',
			icon: map,
			alt: 'Geolocation'
		}
	];
</script>

<div class="app h-full">
	<Toaster {toaster} />
	<div class="flex h-full">
		<!-- Sidebar -->
		<div class="flex w-20 flex-col items-center space-y-4 bg-surface-800 py-4">
			<!-- Logo -->
			<a href="https://espresense.com/companion" target="_blank" class="p-2">
				<img src={logo} class="h-12 w-12" alt="ESPresense Companion" />
			</a>

			<!-- Navigation Links -->
			{#each routes as route}
				<a href="{base}{route.href}" class="rounded-lg p-2 transition-colors {current === `${base}${route.href}` ? 'bg-primary-500 text-white' : 'hover:bg-surface-700'}" title={route.alt}>
					<img src={route.icon} class="h-8 w-8" alt={route.alt} />
				</a>
			{/each}

			<!-- GitHub Link -->
			<a href="https://github.com/ESPresense/ESPresense-companion" target="_blank" class="mt-auto rounded-lg p-2 hover:bg-surface-700">
				<img src={github} class="h-6 w-6" alt="GitHub" />
			</a>
		</div>

		<!-- Main Content -->
		<div class="flex-1 overflow-hidden">
			{@render children()}
		</div>
	</div>
</div>

<style>
	img {
		fill: white;
	}
</style>
