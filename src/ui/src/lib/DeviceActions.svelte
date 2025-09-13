<script lang="ts">
	import { base } from '$app/paths';
	import { detail, calibrateDevice } from '$lib/urls';
	import { getToastStore } from '$lib/toast/toastStore';
	import { showComponent, showConfirm } from '$lib/modal/modalStore';
	import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types';
	import DeviceSettingsModal from './DeviceSettingsModal.svelte';

	export let col: string; // Column identifier from parent table
	export let row: Device; // Device data for this row
	var _ = col; // Suppress unused variable warning while preserving the prop

	const toastStore = getToastStore();
	let loadingEdit = false;
    let loadingDelete = false;

	// Determine if device is active based on lastSeen and timeout
	$: isActive = row.lastSeen && new Date().getTime() - new Date(row.lastSeen).getTime() < (row.timeout || 30000);

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

			showComponent(DeviceSettingsModal, { deviceSetting });
		} catch (ex) {
			console.error('Error fetching device settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading settings: ${ex.message}` : 'An unknown error occurred while loading settings.';

			toastStore.trigger({
				message: errorMessage,
				background: 'preset-filled-error-500'
			});
		} finally {
			loadingEdit = false;
		}
	}

    async function handleDelete() {
        if (!row?.id) return;
        const confirmed = await showConfirm({
            title: 'Delete Device',
            body: `Are you sure you want to delete "${row.name || row.id}"? This cannot be undone.`
        });
        if (!confirmed) return;

        loadingDelete = true;
        try {
            const resp = await fetch(`${base}/api/device/${encodeURIComponent(row.id)}`, { method: 'DELETE' });
            if (!resp.ok && resp.status !== 204) {
                throw new Error(`Failed to delete: ${resp.status} ${resp.statusText}`);
            }
            toastStore.trigger({ message: 'Device deleted', background: 'preset-filled-success-500' });
        } catch (ex) {
            console.error('Delete failed', ex);
            const message = ex instanceof Error ? ex.message : 'Unknown error';
            toastStore.trigger({ message: `Delete failed: ${message}`, background: 'preset-filled-error-500' });
        } finally {
            loadingDelete = false;
        }
    }
</script>

<div class="flex gap-1">
	<button class="btn btn-sm bg-primary-500 hover:bg-primary-600 text-white" on:click|stopPropagation={handleEdit} disabled={loadingEdit} aria-label="Edit device settings">
		{#if loadingEdit}
			<span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
		{:else}
			Edit
		{/if}
	</button>
	{#if isActive}
		<button class="btn btn-sm preset-filled-secondary-500" on:click|stopPropagation={() => detail(row)} aria-label="View device on map"> Map </button>
		<button class="btn btn-sm preset-filled-tertiary-500" on:click|stopPropagation={() => calibrateDevice(row)} aria-label="Calibrate device"> Calibrate </button>
	{/if}
    <button class="btn btn-sm bg-error-500 hover:bg-error-600 text-white" on:click|stopPropagation={handleDelete} disabled={loadingDelete} aria-label="Delete device">
        {#if loadingDelete}
            <span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
        {:else}
            Delete
        {/if}
    </button>
</div>
