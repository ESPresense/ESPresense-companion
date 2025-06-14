<script lang="ts">
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting; // Changed: No longer nullable, assume parent handles loading
	// Removed save logic and imports for base, Accordion, ToastStore etc.
</script>

{#if settings}
	<!-- Removed Accordion, AccordionItem, form, and save button -->
	<!-- Added text-token class for better contrast like in the modal -->
	<!-- Added bg/text classes for inputs like in the modal -->
	<label class="label text-token">
		<span>ID (Original)</span>
		<input class="input bg-surface-100-800-token text-token" type="text" disabled bind:value={settings.originalId} />
	</label>
	<label class="label text-token">
		<span>Alias</span>
		<input class="input bg-surface-100-800-token text-token" type="text" bind:value={settings.id} />
	</label>
	<label class="label text-token">
		<span>Name</span>
		<input class="input bg-surface-100-800-token text-token" type="text" bind:value={settings.name} />
	</label>
	<label class="label text-token">
		<span>RSSI@1m</span>
		<input class="input bg-surface-100-800-token text-token" type="number" placeholder="e.g., -65" bind:value={settings['rssi@1m']} />
	</label>
	
	<div class="text-token font-semibold mt-4 mb-2">Anchor Coordinates (optional)</div>
	<p class="text-sm text-token mb-3">Set X, Y, Z coordinates to anchor this device at a fixed location. Leave empty for normal tracking.</p>
	
	<div class="grid grid-cols-3 gap-3">
		<label class="label text-token">
			<span>X (meters)</span>
			<input class="input bg-surface-100-800-token text-token" type="number" step="0.01" placeholder="0.00" bind:value={settings.x} />
		</label>
		<label class="label text-token">
			<span>Y (meters)</span>
			<input class="input bg-surface-100-800-token text-token" type="number" step="0.01" placeholder="0.00" bind:value={settings.y} />
		</label>
		<label class="label text-token">
			<span>Z (meters)</span>
			<input class="input bg-surface-100-800-token text-token" type="number" step="0.01" placeholder="0.00" bind:value={settings.z} />
		</label>
	</div>
{:else}
	<div class="text-center text-token">Settings not available.</div>
{/if}
