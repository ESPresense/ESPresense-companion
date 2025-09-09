<script lang="ts">
	import { showAllFloors } from '$lib/stores';

	function toggleFilter() {
		$showAllFloors = !$showAllFloors;
	}
</script>

<div class="flex items-center space-x-3">
	<button class="relative inline-flex h-6 w-11 items-center rounded-full transition-colors {$showAllFloors ? 'bg-slate-600' : 'bg-slate-600'}" on:click={toggleFilter} aria-label="Toggle show all floors">
		<span class="inline-block h-4 w-4 transform rounded-full bg-white transition-transform {$showAllFloors ? 'translate-x-6' : 'translate-x-1'}"></span>
	</button>
	<span class="text-white text-sm font-medium">Show All Floors</span>
</div>
