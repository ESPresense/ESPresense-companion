<script lang="ts">
	import { base } from '$app/paths';
	import { detail, calibrateDevice } from '$lib/urls';
	import { Popover } from '@skeletonlabs/skeleton-svelte';
	import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types';
	import DeviceSettingsModal from './DeviceSettingsModal.svelte';
	import { toaster as toastStore } from '$lib/toaster';

	export let col: string; // Column identifier from parent table
	export let row: Device; // Device data for this row
	var _ = col; // Suppress unused variable warning while preserving the prop

	let loadingEdit = false;
	let showSettings = false;
	let deviceSetting: DeviceSetting | null = null;

	async function handleEdit() {
		loadingEdit = true;
		try {
			const response = await fetch(`${base}/api/device/${row.id}`);
			if (!response.ok) throw new Error(`Failed to fetch settings details: ${response.statusText}`);

			const deviceSettingsDetails: DeviceSettingsDetails = await response.json();

			if (!deviceSettingsDetails.settings) {
				throw new Error('Device settings not found in API response.');
			}

			deviceSetting = deviceSettingsDetails.settings;

			deviceSetting = deviceSettingsDetails.settings;
			showSettings = true;
		} catch (ex) {
			console.error('Error fetching device settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading settings: ${ex.message}` : 'An unknown error occurred while loading settings.';

			toastStore.error({
				title: 'Error',
				description: errorMessage
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

{#if showSettings && deviceSetting}
	<Popover on:close={() => showSettings = false} title={`Edit Settings for ${deviceSetting?.name || deviceSetting?.id || ''}`}>
		<DeviceSettingsModal {deviceSetting} on:close={() => showSettings = false} />
	</Popover>
{/if}
