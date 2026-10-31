<script lang="ts">
	import type { DeviceExplanation, ExplainedPlace } from '$lib/types';
	import { ELLIPSE_95_PERCENT, errorColor, rejectionLabel } from '$lib/explain';

	let { explanation }: { explanation: DeviceExplanation | null } = $props();

	const place = (p: ExplainedPlace | null | undefined) => p?.name ?? p?.id ?? 'none';
	const metres = (m: number | null | undefined, digits = 2) => (m == null ? 'n/a' : `${m.toFixed(digits)} m`);
	const percent = (p: number) => `${(p * 100).toFixed(0)}%`;
	const signed = (m: number) => `${m >= 0 ? '+' : ''}${m.toFixed(2)} m`;
</script>

{#if !explanation}
	<p class="text-sm italic p-2">Loading...</p>
{:else}
	<div class="space-y-4 p-2 text-sm" data-testid="device-explain">
		{#if explanation.hints.length > 0}
			<ul class="space-y-1" data-testid="explain-hints">
				{#each explanation.hints as hint}
					<li class="rounded bg-surface-200-800 px-2 py-1">{hint}</li>
				{/each}
			</ul>
		{/if}

		<section>
			<h4 class="font-semibold">1. Heard by</h4>
			{#if explanation.nodes.length === 0}
				<p class="italic">No nodes</p>
			{:else}
				<table class="w-full">
					<tbody>
						{#each explanation.nodes as n (n.id)}
							<tr class={n.current ? '' : 'opacity-50'}>
								<td class="truncate max-w-32" title={n.id}>{n.name ?? n.id}</td>
								<td class="text-right whitespace-nowrap">{n.rssi.toFixed(0)} dBm</td>
								<td class="text-right whitespace-nowrap">{n.distance.toFixed(2)}{n.distVar != null ? ` ± ${Math.sqrt(n.distVar).toFixed(2)}` : ''} m</td>
							</tr>
						{/each}
					</tbody>
				</table>
			{/if}
		</section>

		{#if !explanation.anchored}
			<section>
				<h4 class="font-semibold">2. Locators</h4>
				{#each explanation.scenarios as s (s.name)}
					<details open={s.best} class="border-t border-surface-300-700 py-1">
						<summary class="cursor-pointer">
							{s.best ? '★ ' : ''}{s.name}: {percent(s.probability)}
						</summary>
						<p>Outcome: {s.outcome ?? 'not run'}{s.outcomeReason ? ` (${s.outcomeReason})` : ''}</p>
						<p>Confidence {s.confidence ?? 'n/a'}, room {place(s.room)}, scale {s.scale?.toFixed(2) ?? 'n/a'}</p>
						<table class="w-full">
							<tbody>
								{#each s.nodes as n (n.id)}
									<tr class={n.used ? '' : 'opacity-50'}>
										<td class="truncate max-w-32" title={n.id}>{n.name ?? n.id}</td>
										{#if n.used}
											<td class="text-right whitespace-nowrap">{metres(n.measured)}</td>
											<td class="text-right whitespace-nowrap">
												{#if n.residual != null}
													<span style="color: {errorColor(n.residual / Math.max(n.fitted ?? 1, 1))}">{signed(n.residual)}</span>
												{/if}
											</td>
										{:else}
											<td colspan="2" class="text-right italic">{rejectionLabel(n.reason)}</td>
										{/if}
									</tr>
								{/each}
							</tbody>
						</table>
					</details>
				{:else}
					<p class="italic">No locators have run</p>
				{/each}
			</section>

			<section>
				<h4 class="font-semibold">3. Floor</h4>
				{#each explanation.floors as f, i (f.floor.id)}
					<p>{i === 0 ? '★ ' : ''}{place(f.floor)}: {percent(f.probability)}</p>
				{:else}
					<p class="italic">No floor yet</p>
				{/each}
			</section>
		{/if}

		<section>
			<h4 class="font-semibold">{explanation.anchored ? '2.' : '4.'} Room</h4>
			<p>{place(explanation.room.room)}{explanation.room.room && !explanation.room.inside ? ' (position has drifted outside)' : ''}</p>
			{#if explanation.room.distanceToEdge != null}
				<p>{metres(explanation.room.distanceToEdge)} from the wall</p>
			{/if}
			{#if explanation.room.nearestOtherRoom}
				<p>Nearest other room: {place(explanation.room.nearestOtherRoom)}, {metres(explanation.room.nearestOtherDistance)}</p>
			{/if}
			{#if explanation.room.previousState}
				<p>Was {explanation.room.previousState} until {new Date(explanation.room.stateChanged ?? '').toLocaleTimeString()}</p>
			{/if}
		</section>

		{#if explanation.uncertainty}
			<p class="opacity-75">The ellipse on the map is the 95% range for this fit, reaching up to {metres(explanation.uncertainty.semiMajor * ELLIPSE_95_PERCENT, 1)} from it.</p>
		{/if}
	</div>
{/if}
