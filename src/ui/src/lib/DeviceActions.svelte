<script lang="ts"> // Added lang="ts"
  // import { calibrateById, detail } from '$lib/urls' // calibrateById is no longer needed
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import { getModalStore, getToastStore, type ModalStore, type ToastSettings } from '@skeletonlabs/skeleton'; // Import modal store & ToastSettings
  import type { Device, DeviceSetting, DeviceSettingsDetails } from '$lib/types'; // Import Device, DeviceSetting & DeviceSettingsDetails types
  import DeviceSettingsModal from './DeviceSettingsModal.svelte'; // Import the modal component

  export let col: string;
  export let row: Device;
  var _ = col;

  const modalStore = getModalStore();
  const toastStore = getToastStore();
  let loadingEdit = false; // Loading state for edit button

  function handleCalibrate() {
    // Navigate to the device detail page with the calibration tab selected
    goto(`${base}/devices/${row.id}?tab=calibration`);
  }

  async function handleEdit() {
    loadingEdit = true;
    try {
      const response = await fetch(`${base}/api/device/${row.id}`);
      if (!response.ok) {
        throw new Error(`Failed to fetch settings details: ${response.statusText}`);
      }
      const deviceSettingsDetails: DeviceSettingsDetails = await response.json();

      // Check if the settings object exists within the details
      if (!deviceSettingsDetails.settings) {
        throw new Error('Device settings not found in API response.');
      }

      const deviceSetting: DeviceSetting = deviceSettingsDetails.settings; // Extract the settings

      modalStore.trigger({
        type: 'component',
        // Pass the extracted deviceSetting to the modal
        component: { ref: DeviceSettingsModal, props: { deviceSetting: deviceSetting } },
        // Pass the parent component context if needed for callbacks
        // parent: parentComponentContext, // Uncomment and set if needed
        title: `Edit Settings for ${deviceSetting.name || deviceSetting.id}` // Use extracted settings for title
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
    goto(`${base}/devices/${row.id}?tab=map`);
  }
</script>

<div class="flex gap-1">
  <button class="btn btn-sm bg-primary-700 text-black" on:click|stopPropagation={handleMap}>Map</button>
  <button class="btn btn-sm bg-success-700 text-black" on:click|stopPropagation={handleCalibrate}>Calibrate</button>
  <button class="btn btn-sm bg-warning-700 text-black" on:click|stopPropagation={handleEdit} disabled={loadingEdit}>
    {#if loadingEdit}
      <span class="loading loading-spinner loading-xs"></span>
    {:else}
      Edit
    {/if}
  </button>
</div>
