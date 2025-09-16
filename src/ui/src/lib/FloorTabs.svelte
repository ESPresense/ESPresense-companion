<script lang="ts">
	import { config, showAllFloors } from '$lib/stores';
	import SlideToggle from './SlideToggle.svelte';

	export let floorId: string | null = null;
	$: if (floorId == null) floorId = $config?.floors[0].id;
</script>

<header>
	<svg viewBox="0 0 2 3" aria-hidden="true">
		<path d="M0,0 L1,2 C1.5,3 1.5,3 2,3 L2,0 Z" />
	</svg>
	<nav class="h-50 text-black">
		<div class="flex items-center space-x-4">
			<!-- Floor Tabs -->
			<div class="flex bg-slate-600 rounded-full p-1">
				{#if $config?.floors}
					{#each $config?.floors as { id, name }, index}
						<button class="px-4 py-1 rounded-full text-sm font-medium transition-colors {floorId === id ? 'bg-emerald-400 text-black' : 'text-white hover:bg-slate-500'}" onclick={() => (floorId = id)}>
							{name}
						</button>
					{/each}
				{/if}
			</div>

			<!-- Filter Switch -->
			<div>
				<SlideToggle name="show-all-floors" bind:checked={$showAllFloors}>
					<span class="text-black">Show All Floors</span>
				</SlideToggle>
			</div>
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
