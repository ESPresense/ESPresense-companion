<script lang="ts">
  import { base } from '$app/paths';
  import type { DeviceSetting } from '$lib/types';
  import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';
  import DeviceSettings from './DeviceSettings.svelte'; // Import the refactored component

  // Props
  /** Exposes component props */
  export let parent: any; // The Svelte parent component that triggered the modal
  export let deviceSetting: DeviceSetting; // Passed in from trigger

  const modalStore = getModalStore();
  const toastStore = getToastStore();

  // Create a local copy to avoid directly mutating the prop
  let localSettings = { ...deviceSetting };

  function save() {
    // Ensure rssi@1m is a number or null
    const rssiRef = parseInt(localSettings['rssi@1m'] + '');
    localSettings['rssi@1m'] = isNaN(rssiRef) ? null : rssiRef;

    fetch(`${base}/api/device/${deviceSetting.id}`, { // Use original deviceSetting.id for the PUT request URL
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(localSettings) // Send the local copy
    })
      .then((response) => {
        if (!response.ok) throw new Error(`Save failed: ${response.statusText}`);
        const t: ToastSettings = { message: 'Settings saved successfully!', background: 'variant-filled-success' };
        toastStore.trigger(t);
        // Optionally, update the parent component or state if needed
        if (parent && parent.onSettingsSaved) {
          parent.onSettingsSaved(localSettings);
        }
        modalStore.close(); // Close modal on successful save
      })
      .catch((e) => {
        console.error('Error saving settings:', e);
        let errorMessage = 'An unknown error occurred while saving.';
        if (e instanceof Error) {
          errorMessage = `Error saving: ${e.message}`;
        }
        const t: ToastSettings = { message: errorMessage, background: 'variant-filled-error' };
        toastStore.trigger(t);
      });
  }

  function handleCancel() {
    modalStore.close(); // Close the modal on cancel
  }
</script>

<!-- Added card class for background and padding -->
<div class="card p-4 space-y-4">
  <!-- Use the DeviceSettings component, passing the local state -->
  <!-- Note: DeviceSettings now expects 'originalId' within the settings object -->
  <!-- We need to ensure localSettings includes originalId -->
  <DeviceSettings settings={localSettings} />

  <!-- Modal Actions -->
  <footer class="modal-footer flex justify-end space-x-2 pt-4">
    <button class="btn" on:click={handleCancel}>Cancel</button>
    <button class="btn variant-filled-primary" on:click={save}>Save</button>
  </footer>
</div>
