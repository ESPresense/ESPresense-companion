<script lang="ts">
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting; // Changed: No longer nullable, assume parent handles loading
	// Removed save logic and imports for base, Accordion, ToastStore etc.
</script>

{#if settings}
	<!-- Removed Accordion, AccordionItem, form, and save button -->
	<!-- Added text-token class for better contrast like in the modal -->
	<!-- Added bg/text classes for inputs like in the modal -->
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
{:else}
	<div class="text-center base-font-color">Settings not available.</div>
{/if}
