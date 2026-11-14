<script lang="ts">
	import { apiFetch } from '#lib/api.js';
	import { anchorFitRows, type AnchorFitRow } from '#lib/anchorFit.js';
	import type { OptimizerCycleReport } from '#lib/types.js';

	/** When the optimizer last ran: the last cycle is fetched again whenever this changes. */
	let { lastRunAt }: { lastRunAt?: string } = $props();

	let rows = $state<AnchorFitRow[] | null>(null);
	let loaded = $state(false);

	$effect(() => {
		const _ = lastRunAt; // re-fetch after every cycle
		let cancelled = false;
		apiFetch<OptimizerCycleReport>('/api/optimizer/last-cycle')
			.then((report) => {
				if (cancelled) return;
				rows = anchorFitRows(report);
				loaded = true;
			})
			// 404 before the first cycle, or when another instance holds the optimizer lease: show nothing.
			.catch(() => {
				if (cancelled) return;
				rows = null;
				loaded = false;
			});
		return () => (cancelled = true);
	});
</script>

{#if loaded}
	<div class="border-surface-300-700 mt-3 border-t pt-3 text-sm" data-testid="anchor-fit">
		<div class="font-medium">Fit to anchors</div>
		{#if rows}
			<p class="text-surface-600-400 mt-1">How much the nodes disagree about each anchor's signal level in the last cycle (RMS, dB; lower is better). The anchors' rssi@1m isn't used.</p>
			<table class="mt-2 w-full text-left">
				<thead class="text-surface-600-400">
					<tr>
						<th class="pr-4 font-normal">Calibration</th>
						<th class="pr-4 font-normal">Anchors</th>
						<th class="pr-4 font-normal">RMS (dB)</th>
						<th class="font-normal">vs current</th>
					</tr>
				</thead>
				<tbody>
					{#each rows as row (row.label)}
						<tr>
							<td class="pr-4">
								{row.label}
								{#if row.status}<span class="text-surface-600-400">({row.status})</span>{/if}
							</td>
							<td class="pr-4">{row.anchors}</td>
							<td class="pr-4 font-medium">{row.rms.toFixed(2)}</td>
							<td>{row.versus ?? ''}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		{:else}
			<p class="text-surface-600-400 mt-1">No anchor was heard by at least 3 nodes in the last cycle.</p>
		{/if}
	</div>
{/if}
