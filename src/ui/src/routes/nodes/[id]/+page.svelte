<script lang="ts">
	import { base } from '$app/paths';
	import { nodes } from '$lib/stores';
	import { readable } from 'svelte/store';
	import type { NodeSettingDetails } from '$lib/types';
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';

	import Map from '$lib/Map.svelte';
	import NodeDetailTabs from '$lib/NodeDetailTabs.svelte';
	import NodeSettings from '$lib/NodeSettings.svelte';

	export let floorId: string | null = null;
	export let data: NodeSettingDetails = {};
	$: node = $nodes.find((d) => d.id === data.settings?.id);

	export const nodeDetails = readable([], (set) => {
		async function fetchAndSet() {
			try {
				const response = await fetch(`${base}/api/node/${node?.id}`);
				const result = await response.json();
				set(result.details);
			} catch (ex) {
				console.error(ex);
			}
		}

		fetchAndSet();
		const interval = setInterval(() => {
			fetchAndSet();
		}, 1000);

		return function stop() {
			clearInterval(interval);
		};
	});
</script>

<svelte:head>
	<title>ESPresense Companion: Node Detail</title>
</svelte:head>

<NodeDetailTabs nodeId={data.settings?.id} bind:floorId />

<div class="flex h-full">
	<div class="flex-grow h-full overflow-clip">
		{#if floorId !== 'settings'}
			<Map deviceId="none" nodeId={data.settings?.id} bind:floorId exclusive={true} />
		{/if}
		{#if floorId === 'settings'}
			<NodeSettings settings={data.settings} />
		{/if}
	</div>
	<div class="w-64 z-1 max-h-screen overflow-auto">
		<Accordion>
			<AccordionItem spacing="space-y-4" open>
				<svelte:fragment slot="summary">
					<h3 class="h3">Details</h3>
				</svelte:fragment>
				<svelte:fragment slot="content">
					{#if $nodeDetails}
						{#each $nodeDetails as d}
							<label>
								<span>{d.key}</span>
								<input class="input" type="text" disabled bind:value={d.value} />
							</label>
						{/each}
					{/if}
				</svelte:fragment>
			</AccordionItem>
		</Accordion>
	</div>
</div>
