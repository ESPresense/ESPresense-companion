<script lang="ts">
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting;
</script>

{#if settings}
	<label class="label base-font-color">
		<span>ID (Original)</span>
		<input class="input bg-surface-100-900 base-font-color" type="text" disabled bind:value={settings.originalId} />
	</label>
	<label class="label base-font-color">
		<span>Alias</span>
		<input class="input bg-surface-100-900 base-font-color" type="text" bind:value={settings.id} />
	</label>
	<label class="label base-font-color">
		<span>Name</span>
		<input class="input bg-surface-100-900 base-font-color" type="text" bind:value={settings.name} />
	</label>
	<label class="label base-font-color">
		<span>RSSI@1m</span>
		<input class="input bg-surface-100-900 base-font-color" type="number" placeholder="e.g., -65" bind:value={settings['rssi@1m']} />
	</label>

	<div class="base-font-color font-semibold mt-4 mb-2">Anchor Coordinates (optional)</div>
	<p class="text-sm base-font-color mb-3">Set X, Y, Z coordinates to anchor this device at a fixed location. Leave empty for normal tracking.</p>

	<div class="grid grid-cols-3 gap-3">
		<label class="label base-font-color">
			<span>X (meters)</span>
			<input class="input bg-surface-100-900 base-font-color" type="number" step="0.01" placeholder="0.00" bind:value={settings.x} />
		</label>
		<label class="label base-font-color">
			<span>Y (meters)</span>
			<input class="input bg-surface-100-900 base-font-color" type="number" step="0.01" placeholder="0.00" bind:value={settings.y} />
		</label>
		<label class="label base-font-color">
			<span>Z (meters)</span>
			<input class="input bg-surface-100-900 base-font-color" type="number" step="0.01" placeholder="0.00" bind:value={settings.z} />
		</label>
	</div>
{:else}
	<div class="text-center text-surface-600-400">Settings not available.</div>
{/if}
