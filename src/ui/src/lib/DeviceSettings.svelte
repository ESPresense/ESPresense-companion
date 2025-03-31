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
{:else}
	<div class="text-center text-token">Settings not available.</div>
{/if}
