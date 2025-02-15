<script lang="ts">
	import { page } from '$app/stores';
	import { onMount } from 'svelte';
	import { fetchNode } from '$lib/node';
	import type { Node } from '$lib/types';
	import NodeSettings from '$lib/NodeSettings.svelte';

	let node: Node | null = null;
	let error: string | null = null;

	onMount(async () => {
		try {
			node = await fetchNode($page.params.id);
		} catch (e) {
			error = e instanceof Error ? e.message : String(e);
		}
	});
</script>

{#if error}
	<p class="text-error-500">{error}</p>
{:else if node}
	<h1 class="text-3xl font-bold my-2 px-2">{node.name ?? node.id}</h1>
	<NodeSettings settings={node.settings} />
{:else}
	<p>Loading...</p>
{/if}
