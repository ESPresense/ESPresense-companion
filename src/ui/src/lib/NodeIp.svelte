<script lang="ts">
	import type { Node } from '#lib/types.js';
	interface Props {
		row: Node;
		col: string;
	}

	let { row, col }: Props = $props();
	let _ = $derived(col);
</script>

<div class="flex items-center gap-1">
	<span>{row.telemetry?.ip ?? 'n/a'}</span>
	{#if row.telemetry?.ip}
		<a href="http://{row.telemetry.ip}" target="_blank" class="opacity-70 hover:opacity-100" aria-label="Open node web interface" title="Open node web interface">
			<svg class="w-4 fill-current" viewBox="0 0 24 24" aria-hidden="true"><path d="M14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3m-2 16H5V5h7V3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7h-2v7Z" /></svg>
		</a>
	{/if}
</div>
