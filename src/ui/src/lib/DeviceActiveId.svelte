<script lang="ts">
	import type { Device } from '$lib/types';
	export let row: Device;
	export let col: string;
	$: _ = col;

	// Determine if device is active based on lastSeen and timeout
	$: isActive = row.lastSeen && new Date().getTime() - new Date(row.lastSeen).getTime() < (row.timeout || 30000);
</script>

<div class="flex items-center gap-2">
	<span class="{isActive ? 'bg-green-500' : 'bg-red-500'} w-3 h-3 rounded-full inline-block flex-shrink-0"></span>
	<span class="truncate">{row.name || row.id}</span>
</div>