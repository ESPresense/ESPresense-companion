<script lang="ts">
	import { base } from '$app/paths';
	import { detail, calibrateDevice } from '$lib/urls'; // Import detail and calibrateDevice functions
	import { getModalStore, getToastStore, type ModalStore, type ToastSettings } from '@skeletonlabs/skeleton'; // Import modal store & ToastSettings
	import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types'; // Import Device, DeviceSetting & DeviceSettingsDetails types
	import DeviceSettingsModal from './DeviceSettingsModal.svelte'; // Import the modal component

	export let col: string;
	export let row: Device;
	var _ = col;

	const modalStore = getModalStore();
	const toastStore = getToastStore();
	let loadingEdit = false; // Loading state for edit button

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
				component: { ref: DeviceSettingsModal, props: { deviceSetting: deviceSetting } },
				title: `Edit Settings for ${deviceSetting.name || deviceSetting.id}`
			});
		} catch (ex) {
			console.error('Error fetching device settings for modal:', ex);
			let errorMessage = 'An unknown error occurred while loading settings.';
			if (ex instanceof Error) {
				errorMessage = `Error loading settings: ${ex.message}`;
			}
			const t: ToastSettings = { message: errorMessage, background: 'variant-filled-error' };
			toastStore.trigger(t);
		} finally {
			loadingEdit = false;
		}
	}

	function handleMap() {
		detail(row);
	}
</script>

<div class="flex gap-1">
	<button class="btn btn-sm variant-filled-primary" on:click|stopPropagation={handleEdit} disabled={loadingEdit}>
		{#if loadingEdit}
			<span class="loading loading-spinner loading-xs"></span>
		{:else}
			Edit
		{/if}
	</button>
	<button class="btn btn-sm variant-filled-secondary" on:click|stopPropagation={handleMap}>Map</button>
	<button class="btn btn-sm variant-filled-tertiary" on:click|stopPropagation={() => calibrateDevice(row)}>Calibrate</button>
</div>
