<script lang="ts">
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting; // Changed: No longer nullable, assume parent handles loading
</script>

{#if settings}
	<label class="label text-surface-950-50">
		<span>ID (Original)</span>
		<input class="input bg-surface-100-900 text-surface-950-50" type="text" disabled bind:value={settings.originalId} />
	</label>
	<label class="label text-surface-950-50">
		<span>Alias</span>
		<input class="input bg-surface-100-900 text-surface-950-50" type="text" bind:value={settings.id} />
	</label>
	<label class="label text-surface-950-50">
		<span>Name</span>
		<input class="input bg-surface-100-900 text-surface-950-50" type="text" bind:value={settings.name} />
	</label>
	<label class="label text-surface-950-50">
		<span>RSSI@1m</span>
		<input class="input bg-surface-100-900 text-surface-950-50" type="number" placeholder="e.g., -65" bind:value={settings['rssi@1m']} />
	</label>
{:else}
	<div class="text-center text-surface-950-50">Settings not available.</div>
{/if}
