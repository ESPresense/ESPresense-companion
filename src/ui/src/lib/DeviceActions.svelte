<script lang="ts">
	import { base } from '$app/paths';
	import { detail, calibrateDevice } from '$lib/urls';
	import { getToastStore } from '$lib/toast/toastStore';
	import { showComponent } from '$lib/modal/modalStore';
	import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types';
	import DeviceSettingsModal from './DeviceSettingsModal.svelte';

	export let col: string; // Column identifier from parent table
	export let row: Device; // Device data for this row
	var _ = col; // Suppress unused variable warning while preserving the prop

	const toastStore = getToastStore();
	let loadingEdit = false;

	async function handleEdit() {
		loadingEdit = true;
		try {
			const response = await fetch(`${base}/api/device/${row.id}`);
			if (!response.ok) throw new Error(`Failed to fetch settings details: ${response.statusText}`);

			const deviceSettingsDetails: DeviceSettingsDetails = await response.json();

			if (!deviceSettingsDetails.settings) {
				throw new Error('Device settings not found in API response.');
			}

			const deviceSetting: DeviceSetting = deviceSettingsDetails.settings;

			showComponent(DeviceSettingsModal, { deviceSetting });
		} catch (ex) {
			console.error('Error fetching device settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading settings: ${ex.message}` : 'An unknown error occurred while loading settings.';

			toastStore.trigger({
				message: errorMessage,
				background: 'variant-filled-error'
			});
		} finally {
			loadingEdit = false;
		}
	}
</script>

<div class="flex gap-1">
	<button class="btn btn-sm variant-filled-primary" on:click|stopPropagation={handleEdit} disabled={loadingEdit} aria-label="Edit device settings">
		{#if loadingEdit}
			<span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
		{:else}
			Edit
		{/if}
	</button>
	<button class="btn btn-sm variant-filled-secondary" on:click|stopPropagation={() => detail(row)} aria-label="View device on map"> Map </button>
	<button class="btn btn-sm variant-filled-tertiary" on:click|stopPropagation={() => calibrateDevice(row)} aria-label="Calibrate device"> Calibrate </button>
</div>
