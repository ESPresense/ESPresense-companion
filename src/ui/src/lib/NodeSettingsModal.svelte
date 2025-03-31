<script lang="ts">
  import { base } from '$app/paths';
  import type { NodeSetting } from '$lib/types';
  import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';
  import NodeSettings from './NodeSettings.svelte'; // Import the fields component

  // Props
  export let parent: any; // The Svelte parent component that triggered the modal
  export let nodeSetting: NodeSetting; // Passed in from trigger

  const modalStore = getModalStore();
  const toastStore = getToastStore();

  // Create a local copy to avoid directly mutating the prop
  // Ensure all nested objects are copied as well if they exist
  let localSettings = JSON.parse(JSON.stringify(nodeSetting));

  async function save() {
    // Add any necessary validation or data transformation here if needed

    try {
      const response = await fetch(`${base}/api/node/${nodeSetting.id}`, { // Use original nodeSetting.id for the PUT request URL
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(localSettings) // Send the local copy
      });

      if (!response.ok) throw new Error(`Save failed: ${response.statusText}`);

      const t: ToastSettings = { message: 'Node settings saved successfully!', background: 'variant-filled-success' };
      toastStore.trigger(t);

      // Optionally, update the parent component or state if needed
      if (parent && parent.onSettingsSaved) {
        parent.onSettingsSaved(localSettings);
      }
      modalStore.close(); // Close modal on successful save
    } catch (e) {
      console.error('Error saving node settings:', e);
      let errorMessage = 'An unknown error occurred while saving.';
      if (e instanceof Error) {
        errorMessage = `Error saving: ${e.message}`;
      }
      const t: ToastSettings = { message: errorMessage, background: 'variant-filled-error' };
      toastStore.trigger(t);
    }
  }

  function handleCancel() {
    modalStore.close(); // Close the modal on cancel
  }
</script>

<!-- Reusing structure from DeviceSettingsModal -->
<div class="card p-4 space-y-4">
  <!-- Use the NodeSettings component, passing the local state -->
  <NodeSettings settings={localSettings} />

  <!-- Modal Actions -->
  <footer class="modal-footer flex justify-end space-x-2 pt-4">
    <button class="btn" on:click={handleCancel}>Cancel</button>
    <button class="btn variant-filled-primary" on:click={save}>Save</button>
  </footer>
</div>
