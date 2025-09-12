<script lang="ts">
	import type { Node } from '$lib/types';
	export let row: Node;
	export let col: string;
	$: _ = col;
</script>

<div class="flex items-center gap-2">
	<span class="{row.online ? 'bg-green-500' : 'bg-red-500'} w-3 h-3 rounded-full inline-block flex-shrink-0"></span>
	<span class="truncate">{row.name || row.id}</span>
</div>