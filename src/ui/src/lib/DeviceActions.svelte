<script lang="ts">
	import { base } from '$app/paths';
	import { detail, calibrateDevice } from '$lib/urls';
	import { getModalStore, getToastStore, type ModalStore, type ToastSettings } from '$lib/utils/skeleton';
	import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types';
	import DeviceSettingsModal from './DeviceSettingsModal.svelte';

	export let col: string; // Column identifier from parent table
	export let row: Device; // Device data for this row
	var _ = col; // Suppress unused variable warning while preserving the prop

	const modalStore = getModalStore();
	const toastStore = getToastStore();
	let loadingEdit = false;

	async function handleEdit() {
		loadingEdit = true;
		try {
			const response = await fetch(`${base}/api/device/${row.id}`);
			if (!response.ok) {
				throw new Error(`Failed to fetch settings details: ${response.statusText}`);
			}

			const deviceSettingsDetails: DeviceSettingsDetails = await response.json();

			if (!deviceSettingsDetails.settings) {
				throw new Error('Device settings not found in API response.');
			}

			const deviceSetting: DeviceSetting = deviceSettingsDetails.settings;

			modalStore.trigger({
				type: 'component',
				component: { ref: DeviceSettingsModal, props: { deviceSetting } },
				title: `Edit Settings for ${deviceSetting.name || deviceSetting.id}`
			});
		} catch (ex) {
			console.error('Error fetching device settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading settings: ${ex.message}` : 'An unknown error occurred while loading settings.';

			toastStore.create({
				description: errorMessage,
				type: 'error'
			});
		} finally {
			loadingEdit = false;
		}
	}
</script>

<div class="flex gap-1">
	<button class="btn btn-sm preset-filled-primary-500" on:click|stopPropagation={handleEdit} disabled={loadingEdit} aria-label="Edit device settings">
		{#if loadingEdit}
			<span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
		{:else}
			Edit
		{/if}
	</button>
	<button class="btn btn-sm preset-filled-secondary-500" on:click|stopPropagation={() => detail(row)} aria-label="View device on map"> Map </button>
	<button class="btn btn-sm preset-filled-tertiary-500" on:click|stopPropagation={() => calibrateDevice(row)} aria-label="Calibrate device"> Calibrate </button>
</div>
