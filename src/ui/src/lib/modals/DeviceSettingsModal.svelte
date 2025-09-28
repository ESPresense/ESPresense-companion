<script lang="ts">
	import { resolve } from '$app/paths';
	import type { DeviceSetting } from '$lib/types';
	import { getToastStore } from '$lib/toast/toastStore';
	import { createEventDispatcher } from 'svelte';
	import DeviceSettings from '../DeviceSettings.svelte'; // Import the refactored component

	// Props
	export let parent: any = undefined; // The Svelte parent component that triggered the modal (for backward compatibility)
	export let deviceSetting: DeviceSetting; // Passed in from trigger

	const dispatch = createEventDispatcher();
	const toastStore = getToastStore();

	// Create a local copy to avoid directly mutating the prop
	let localSettings = { ...deviceSetting };
	let isSaving = false; // Track saving state

	async function save() {
		try {
			isSaving = true;

			// Ensure rssi@1m is a number or null
			const rssiRef = Math.floor(parseFloat((localSettings['rssi@1m'] ?? '') + ''));
			localSettings['rssi@1m'] = isNaN(rssiRef) ? null : rssiRef;

			// Normalize anchor coordinates (allow empty values to clear anchor)
			const normalizeCoordinate = (value: unknown) => {
				if (value === '' || value === null || value === undefined) return null;
				const numeric = parseFloat(String(value));
				return isNaN(numeric) ? null : numeric;
			};

			localSettings.x = normalizeCoordinate(localSettings.x);
			localSettings.y = normalizeCoordinate(localSettings.y);
			localSettings.z = normalizeCoordinate(localSettings.z);

			const response = await fetch(resolve(`/api/device/${deviceSetting.id}`), {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify(localSettings)
			});

			if (!response.ok) throw new Error(`Save failed: ${response.statusText}`);

			toastStore.trigger({
				message: 'Settings saved successfully!',
				background: 'preset-filled-success-500'
			});

			// Optionally, update the parent component or state if needed
			if (parent && parent.onSettingsSaved) {
				parent.onSettingsSaved(localSettings);
			}

			// Close modal - either via parent.onClose or dispatch close event
			if (parent && parent.onClose) {
				parent.onClose();
			} else {
				dispatch('close');
			}
		} catch (error) {
			console.error('Error saving settings:', error);
			let errorMessage = 'An unknown error occurred while saving.';

			if (error instanceof Error) {
				errorMessage = `Error saving: ${error.message}`;
			}

			toastStore.trigger({
				message: errorMessage,
				background: 'preset-filled-error-500'
			});
		} finally {
			isSaving = false;
		}
	}

	function handleCancel() {
		// Close modal - either via parent.onClose or dispatch close event
		if (parent && parent.onClose) {
			parent.onClose();
		} else {
			dispatch('close');
		}
	}
</script>

<!-- Modal content - styling handled by ComponentModal wrapper -->
<div class="space-y-4">
	<header class="text-xl font-bold mb-4">
		Edit Settings for {deviceSetting.name || deviceSetting.id}
	</header>

	<!-- Use the DeviceSettings component, passing the local state -->
	<DeviceSettings settings={localSettings} />

	<!-- Modal Actions -->
	<footer class="modal-footer flex justify-end space-x-2 pt-4">
		<button class="btn" onclick={handleCancel} disabled={isSaving}>Cancel</button>
		<button class="btn preset-filled-primary-500" onclick={save} disabled={isSaving}>
			{#if isSaving}
				Saving...
			{:else}
				Save
			{/if}
		</button>
	</footer>
</div>
