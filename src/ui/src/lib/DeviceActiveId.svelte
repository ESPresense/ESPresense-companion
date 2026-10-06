<script lang="ts">
	import type { Device } from '$lib/types';
	interface Props {
		row: Device;
		col: string;
	}

	let { row, col }: Props = $props();
	let _ = $derived(col);

	// Determine if device is active based on lastSeen and timeout
	let isActive = $derived(row.lastSeen && new Date().getTime() - new Date(row.lastSeen).getTime() < (row.timeout || 30000));
</script>

<div class="flex items-center gap-2">
	<span class="{isActive ? 'bg-green-500' : 'bg-red-500'} w-3 h-3 rounded-full inline-block flex-shrink-0"></span>
	<span class="truncate">{row.name || row.id}</span>
</div>
