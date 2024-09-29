<script lang="ts">
	import { config } from '$lib/stores';

	import { RadioGroup, RadioItem } from '@skeletonlabs/skeleton';
	import Filter from './Filter.svelte';

	export let floorId: string | null = null;
	$: if (floorId == null) floorId = $config?.floors[0].id;
</script>

<header>
	<svg viewBox="0 0 2 3" aria-hidden="true">
		<path d="M0,0 L1,2 C1.5,3 1.5,3 2,3 L2,0 Z" />
	</svg>
	<nav class="h-50 text-black">
		<RadioGroup active="variant-filled-primary" hover="hover:variant-soft-primary">
			{#if $config?.floors}
				{#each $config?.floors as { id, name }}
					<RadioItem bind:group={floorId} {name} value={id}>{name}</RadioItem>
				{/each}
			{/if}
		</RadioGroup>
		<div class="pl-4 pt-2">
			<Filter />
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
