<script lang="ts">
  import { base } from '$app/paths';
  import type { NodeSetting } from '$lib/types';
  import { getToastStore } from '$lib/toast/toastStore';
  import { createEventDispatcher } from 'svelte';
  import NodeSettings from './NodeSettings.svelte'; // Import the fields component

  // Props
  export let parent: any = undefined; // The Svelte parent component that triggered the modal (for backward compatibility)
  export let nodeSetting: NodeSetting; // Passed in from trigger

  const dispatch = createEventDispatcher();
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

      // Close modal - either via parent.onClose or dispatch close event
      if (parent && parent.onClose) {
        parent.onClose();
      } else {
        dispatch('close');
      }
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
    // Close modal - either via parent.onClose or dispatch close event
    if (parent && parent.onClose) {
      parent.onClose();
    } else {
      dispatch('close');
    }
  }
</script>

<!-- Reusing structure from DeviceSettingsModal -->
<div class="card p-4 space-y-4">
  <header class="text-xl font-bold mb-4">
    Edit Settings for {nodeSetting.name || nodeSetting.id}
  </header>

  <!-- Use the NodeSettings component, passing the local state -->
  <NodeSettings settings={localSettings} />

  <!-- Modal Actions -->
  <footer class="modal-footer flex justify-end space-x-2 pt-4">
    <button class="btn" on:click={handleCancel}>Cancel</button>
    <button class="btn variant-filled-primary" on:click={save}>Save</button>
  </footer>
</div>
