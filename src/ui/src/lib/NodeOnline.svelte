<script lang="ts">
	import type { Node } from '$lib/types';
	export let row: Node;
	export let col: string;
	$: _ = col;
</script>

<div class="flex justify-center">
	<span class="{row.online ? 'bg-green-500' : 'bg-red-500'} w-4 h-4 rounded-full inline-block"></span>
</div>
