<script lang="ts">
	import { onMount } from 'svelte';
	import { apiFetch } from '$lib/api';
	import type { PositionAccuracy } from '$lib/types';

	let accuracy = $state<PositionAccuracy | null>(null);

	async function refresh() {
		try {
			accuracy = await apiFetch<PositionAccuracy>('/api/accuracy');
		} catch (ex) {
			console.error(`Error fetching position accuracy: ${ex instanceof Error ? ex.message : String(ex)}`);
		}
	}

	onMount(() => {
		refresh();
		const interval = setInterval(refresh, 60000);
		return () => clearInterval(interval);
	});

	const metres = (m: number | null | undefined) => (m == null ? 'n/a' : `${m.toFixed(1)} m`);
</script>

{#if accuracy}
	<div class="px-4 pb-4 space-y-1" data-testid="position-accuracy">
		<p class="text-lg">
			<span class="font-semibold">Typical error {metres(accuracy.median)}</span>
			{#if accuracy.worst}
				<span class="opacity-75">· worst node {accuracy.worst.name ?? accuracy.worst.id}, links {metres(accuracy.worst.linkError)} off · 95% within {metres(accuracy.p95)}</span>
			{/if}
		</p>
		<p>{accuracy.guidance}</p>
		{#each accuracy.hints as hint}
			<p>{hint}</p>
		{/each}
		<p class="text-sm opacity-75">Each node is hidden and located from its neighbours ({accuracy.solved} of {accuracy.placed} solved). This tests node-to-node distances, so devices may do better or worse.</p>
	</div>
{/if}
