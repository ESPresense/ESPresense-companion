<script lang="ts">
	import type { Node } from '$lib/types';
	interface Props {
		row: Node;
		col: string;
	}

	let { row, col }: Props = $props();
	let _ = $derived(col);
</script>

<div class="flex justify-center">
	<span class="{row.online ? 'bg-green-500' : 'bg-red-500'} w-4 h-4 rounded-full inline-block"></span>
</div>
