<script lang="ts">
	import type { NodeSetting } from './types';

	export let settings: NodeSetting; // Assume parent handles loading and provides this
</script>

{#if settings}
	<div class="space-y-4">
		<label class="label">
			<span class="text-surface-800-200">ID</span>
			<input class="input" type="text" disabled bind:value={settings.id} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Name</span>
			<input class="input" type="text" bind:value={settings.name} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Absorption</span>
			<input class="input" type="number" bind:value={settings.calibration.absorption} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Rx Adj Rssi</span>
			<input class="input" type="number" bind:value={settings.calibration.rxAdjRssi} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Tx Ref Rssi</span>
			<input class="input" type="number" bind:value={settings.calibration.txRefRssi} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Max Distance</span>
			<input class="input" type="number" bind:value={settings.filtering.maxDistance} />
		</label>
	</div>
{:else}
	<div class="text-center text-surface-600-400">Node settings not available.</div>
{/if}
