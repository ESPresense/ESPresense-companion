<script lang="ts">
	import { gotoMap } from '$lib/urls';
	import { config } from '$lib/stores';
	import type { Node } from '$lib/types';

	export let nodeName: string = 'Unknown Node';
	export let currentFloorId: string | null = null;
	export let node: Node | undefined = undefined;

	// Helper function to shorten very long node IDs
	function getDisplayName(name: string) {
		if (!name || name === 'Unknown Node') return 'Unknown Node';
		
		// If it's a very long ID, show first part + "..." + last part
		if (name.length > 40) {
			return `${name.substring(0, 20)}...${name.substring(name.length - 12)}`;
		}
		return name;
	}

	// Get floors that this node is actually on
	$: nodeFloors = node?.floors || [];
	$: availableFloors = $config?.floors?.filter(f => nodeFloors.includes(f.id)) || [];
	$: showFloorSelection = availableFloors.length > 1;
</script>

<div class="flex flex-col space-y-2 px-4 py-2">
	<!-- Breadcrumb Navigation -->
	<div class="flex items-center space-x-2 text-sm text-surface-600-400">
		<button 
			class="hover:text-primary-500 transition-colors" 
			onclick={() => gotoMap()}
			aria-label="Go to map"
		>
			Map
		</button>
		
		<span>→</span>
		
		<span class="text-surface-900-100 font-medium" title={nodeName}>
			{getDisplayName(nodeName)}
		</span>
		
	</div>

	<!-- Floor Tabs (only show if node is on multiple floors) -->
	{#if showFloorSelection}
		<div class="flex items-center space-x-4">
			<div class="flex bg-slate-600 rounded-full p-1">
				{#each availableFloors as floor}
					<button 
						class="px-4 py-1 rounded-full text-sm font-medium transition-colors {currentFloorId === floor.id ? 'bg-emerald-400 text-black' : 'text-white hover:bg-slate-500'}"
						onclick={() => currentFloorId = floor.id}
					>
						{floor.name}
					</button>
				{/each}
			</div>
		</div>
	{/if}
</div>
