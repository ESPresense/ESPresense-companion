<script lang="ts">
  import { base } from '$app/paths';
  import type { DeviceSetting } from '$lib/types';
  import { getModalStore, getToastStore, type ToastSettings } from '$lib/utils/skeleton';
  import DeviceSettings from './DeviceSettings.svelte'; // Import the refactored component

	// Props
	/** Exposes component props */
	export let parent: any; // The Svelte parent component that triggered the modal
	export let deviceSetting: DeviceSetting; // Passed in from trigger

	const modalStore = getModalStore();
	const toastStore = getToastStore();

	// Create a local copy to avoid directly mutating the prop
	let localSettings = { ...deviceSetting };
	let isSaving = false; // Track saving state

	async function save() {
		try {
			isSaving = true;

			// Ensure rssi@1m is a number or null
			const rssiRef = Math.floor(parseFloat(localSettings['rssi@1m'] + ''));
			localSettings['rssi@1m'] = isNaN(rssiRef) ? null : rssiRef;

			const response = await fetch(`${base}/api/device/${deviceSetting.id}`, {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify(localSettings)
			});

			if (!response.ok) throw new Error(`Save failed: ${response.statusText}`);

			toastStore.create({
				description: 'Settings saved successfully!',
				type: 'success'
			});

			// Optionally, update the parent component or state if needed
			if (parent && parent.onSettingsSaved) {
				parent.onSettingsSaved(localSettings);
			}

			if (parent && parent.onClose) parent.onClose(); // Close modal on successful save
		} catch (error) {
			console.error('Error saving settings:', error);
			let errorMessage = 'An unknown error occurred while saving.';

			if (error instanceof Error) {
				errorMessage = `Error saving: ${error.message}`;
			}

			toastStore.create({
				description: errorMessage,
				type: 'error'
			});
		} finally {
			isSaving = false;
		}
	}

	function handleCancel() {
		modalStore.close(); // Close the modal on cancel
	}
</script>

<!-- Added card class for background and padding -->
<div class="card p-4 space-y-4">
	<!-- Use the DeviceSettings component, passing the local state -->
	<DeviceSettings settings={localSettings} />

	<!-- Modal Actions -->
	<footer class="modal-footer flex justify-end space-x-2 pt-4">
		<button class="btn" on:click={handleCancel}>Cancel</button>
		<button class="btn preset-filled-primary-500" on:click={save}>
			{#if isSaving}
				Saving...
			{:else}
				Save
			{/if}</button
		>
	</footer>
</div>
