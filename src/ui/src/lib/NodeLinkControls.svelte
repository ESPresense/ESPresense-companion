<script lang="ts">
	import SlideToggle from './SlideToggle.svelte';
	import { errorColor } from './explain';
	import type { LinkDirection } from './nodeLinks';

	interface Props {
		enabled: boolean;
		nearest: number | null;
		direction: LinkDirection;
	}

	let { enabled = $bindable(), nearest = $bindable(), direction = $bindable() }: Props = $props();

	const nearestOptions = [2, 3, 4, 6, 0];
</script>

<div class="fixed bottom-16 right-4 flex flex-col gap-2 rounded bg-surface-100-900/90 p-3 text-sm shadow" style="z-index: 2;" data-testid="node-link-controls">
	<SlideToggle name="node-links" bind:checked={enabled} size="sm">
		<span>Node links</span>
	</SlideToggle>
	{#if enabled}
		<label class="flex items-center justify-between gap-2">
			<span>Nearest</span>
			<select class="select w-24 py-0.5" value={nearest ?? 0} onchange={(e) => (nearest = Number(e.currentTarget.value) || null)} aria-label="Nearest neighbours per node">
				{#each nearestOptions as n}
					<option value={n}>{n ? n : 'All'}</option>
				{/each}
			</select>
		</label>
		<label class="flex items-center justify-between gap-2">
			<span>Show</span>
			<select class="select w-24 py-0.5" bind:value={direction} aria-label="Link direction filter">
				<option value="all">All</option>
				<option value="long">Long</option>
				<option value="short">Short</option>
			</select>
		</label>
		<div class="flex items-center gap-1 text-xs" aria-hidden="true">
			<span style="color: {errorColor(-1)}">short</span>
			<span class="h-1.5 w-16 rounded" style="background: linear-gradient(to right, {errorColor(-1)}, {errorColor(0)}, {errorColor(1)})"></span>
			<span style="color: {errorColor(1)}">long</span>
		</div>
		<p class="max-w-48 text-xs opacity-70">Measured (real) metres, worse direction. Dashed: an anchored device. Hover a node to see only its links.</p>
	{/if}
</div>
