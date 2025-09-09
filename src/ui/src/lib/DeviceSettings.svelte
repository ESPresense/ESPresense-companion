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
	</div>
{:else}
	<div class="text-center text-surface-600-400">Settings not available.</div>
{/if}
