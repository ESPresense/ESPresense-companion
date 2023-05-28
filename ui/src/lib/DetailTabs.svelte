<script lang="ts">
	import { config, devices, history } from './stores';
	import { goto } from '$app/navigation';
	import { base } from '$app/paths';
	import { RadioGroup, RadioItem } from '@skeletonlabs/skeleton';

	export let floorId: string | null = null;
	export let deviceId: string | null = null;
	export let tab = 'map';

	$: device = $devices.find((d) => d.id === deviceId);
	$: floor = $config?.floors.find((f) => f.id === floorId);

	function goBack(defaultRoute = base) {
		goto($history.length >= 2 ? $history[1] : defaultRoute);
	}
</script>

<header>
	<svg viewBox="0 0 2 3" aria-hidden="true">
		<path d="M0,0 L1,2 C1.5,3 1.5,3 2,3 L2,0 Z" />
	</svg>
	<nav class="h-50 text-black">
		<button on:click={() => goBack()}>
		<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" stroke-width="1.5" stroke="currentColor" class="w-6 h-6">
			<path stroke-linecap="round" stroke-linejoin="round" d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18" />
			</svg>
		</button>
		{#if device}
			<div class="pl-4 px-4 py-1">
				<h4 class="h4">{device?.name} on {floor?.name}</h4>
			</div>
		{/if}
		<RadioGroup active="variant-filled-primary" hover="hover:variant-soft-primary">
			<RadioItem bind:group={tab} name="Map" value="map">Map</RadioItem>
			<RadioItem bind:group={tab} name="Details" value="details">Details</RadioItem>
		</RadioGroup>
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
