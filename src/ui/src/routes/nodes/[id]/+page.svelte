<script lang="ts">
	import { resolve } from '$app/paths';
	import { nodes } from '$lib/stores';
	import { readable } from 'svelte/store';
	import type { NodeSettingDetails } from '$lib/types';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';

	import Map from '$lib/Map.svelte';
	import NodeBreadcrumb from '$lib/NodeBreadcrumb.svelte';

	export let floorId: string | null = null;
	export let data: NodeSettingDetails = {};
	$: node = $nodes.find((d) => d.id === data.settings?.id);

	// Initialize floorId to the first floor the node is actually on
	$: if (!floorId && node?.floors?.length > 0) {
		floorId = node.floors[0];
	}

	// Accordion state for Skeleton v4 using the Svelte $state rune
	let accordionValue = $state(['details']);

	export const nodeDetails = readable([], (set) => {
		async function fetchAndSet() {
			try {
				const response = await fetch(resolve(`/api/node/${node?.id}`));
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

<div class="flex h-full min-h-0">
	<div class="flex flex-col flex-grow min-h-0">
		<!-- Breadcrumb Navigation -->
		<NodeBreadcrumb nodeName={node?.name || node?.id || 'Unknown Node'} bind:currentFloorId={floorId} {node} />

		<div class="grid flex-1 min-h-0">
			<Map deviceId="none" nodeId={data.settings?.id} bind:floorId exclusive={true} />
		</div>
	</div>
	<div class="w-64 z-1 max-h-screen overflow-auto">
		<Accordion value={accordionValue} collapsible onValueChange={({ value }) => (accordionValue = value)}>
			<Accordion.Item value="details">
				<Accordion.ItemTrigger>
					<h3 class="h3">Details</h3>
				</Accordion.ItemTrigger>
				<Accordion.ItemContent>
					<div class="space-y-4">
						{#if $nodeDetails}
							{#each $nodeDetails as d}
								<label class="flex flex-col gap-1">
									<span>{d.key}</span>
									<input class="input" type="text" disabled bind:value={d.value} />
								</label>
							{/each}
						{/if}
					</div>
				</Accordion.ItemContent>
			</Accordion.Item>
		</Accordion>
	</div>
</div>
