<script lang="ts">
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting; // Changed: No longer nullable, assume parent handles loading
	// Removed save logic and imports for base, Accordion, ToastStore etc.
</script>

{#if settings}
	<div class="space-y-4">
		<label class="label">
			<span class="text-surface-800-200">ID (Original)</span>
			<input class="input" type="text" disabled bind:value={settings.originalId} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Alias</span>
			<input class="input" type="text" bind:value={settings.id} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Name</span>
			<input class="input" type="text" bind:value={settings.name} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">RSSI@1m</span>
			<input class="input" type="number" placeholder="e.g., -65" bind:value={settings['rssi@1m']} />
		</label>
		<div class="grid gap-3 sm:grid-cols-3">
			<label class="label">
				<span class="text-surface-800-200">X (meters)</span>
				<input class="input" type="number" step="0.01" placeholder="e.g., 1.25" bind:value={settings.x} />
			</label>
			<label class="label">
				<span class="text-surface-800-200">Y (meters)</span>
				<input class="input" type="number" step="0.01" placeholder="e.g., 2.50" bind:value={settings.y} />
			</label>
			<label class="label">
				<span class="text-surface-800-200">Z (meters)</span>
				<input class="input" type="number" step="0.01" placeholder="e.g., 0.75" bind:value={settings.z} />
			</label>
		</div>
	</div>
{:else}
	<div class="text-center text-surface-600-400">Settings not available.</div>
{/if}
