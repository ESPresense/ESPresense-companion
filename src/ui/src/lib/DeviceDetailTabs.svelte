<script lang="ts">
	import { config, devices } from './stores';
	import { goto, afterNavigate } from '$app/navigation';
	import { base } from '$app/paths';

	let {
		floorId = null,
		deviceId = null,
		tab = $bindable('map')
	}: {
		floorId: string | null;
		deviceId: string | null;
		tab: string;
	} = $props();

	let device = $derived($devices?.find((d) => d.id === deviceId));
	let floor = $derived($config?.floors.find((f) => f.id === floorId));
	let previousPage: string | undefined = undefined;

	// Helper function to shorten very long device IDs
	function getDisplayName(device: any) {
		if (device?.name) return device.name;
		if (!device?.id) return 'Unknown Device';
		
		const id = device.id;
		// If it's a very long ID (like iBeacon), show first part + "..." + last part
		if (id.length > 30) {
			return `${id.substring(0, 15)}...${id.substring(id.length - 8)}`;
		}
		return id;
	}

	afterNavigate(({ from }) => {
		previousPage = from?.url?.pathname;
	});

	function goBack(defaultRoute = base + '/') {
		goto(previousPage || defaultRoute);
	}
</script>

<header>
	<svg viewBox="0 0 2 3" aria-hidden="true">
		<path d="M0,0 L1,2 C1.5,3 1.5,3 2,3 L2,0 Z" />
	</svg>
	<nav class="h-50 text-black">
		<button onclick={() => goBack()} aria-label="Go back">
			<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor" class="w-6 h-6">
				<path stroke-linecap="round" stroke-linejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
			</svg>
		</button>
		{#if device}
			<div class="pl-4 px-4 py-1">
				<h4 class="text-lg font-semibold truncate" title="{device?.name || device?.id} on {floor?.name ?? 'Unknown'}">{getDisplayName(device)} on {floor?.name ?? 'Unknown'}</h4>
			</div>
		{/if}
		<div class="flex bg-slate-600 rounded-full p-1">
			<button class="px-6 py-2 rounded-full text-sm font-medium transition-colors {tab === 'map' ? 'bg-emerald-400 text-black' : 'text-white hover:bg-slate-500'}" onclick={() => (tab = 'map')}> Map </button>
			<button class="px-6 py-2 rounded-full text-sm font-medium transition-colors {tab === 'calibration' ? 'bg-emerald-400 text-black' : 'text-white hover:bg-slate-500'}" onclick={() => (tab = 'calibration')}> Calibration </button>
		</div>
	</nav>
	<svg viewBox="0 0 2 3" aria-hidden="true">
		<path d="M0,0 L0,3 C0.5,3 0.5,3 1,2 L2,0 Z" />
	</svg>
</header>

<style>
	header {
		position: absolute;
		left: 0;
		right: 0;
		top: 0;
		display: flex;
		justify-content: center;
		--background: rgba(255, 255, 255, 0.7);
	}

	svg {
		z-index: 1;
		width: 2em;
		height: 3em;
		display: block;
	}

	path {
		fill: var(--background);
	}

	nav {
		z-index: 1;
		position: relative;
		padding: 0;
		margin: 0;
		height: 3em;
		display: flex;
		justify-content: center;
		align-items: center;
		list-style: none;
		background: var(--background);
		background-size: contain;
	}
</style>
