<script lang="ts">
  import { onMount } from 'svelte';
  import { drawerStore } from '@skeletonlabs/skeleton';

  export let deviceId = '';
  type DeviceSetting = {
    originalId: string;
    id: string | null;
    name: string | null;
    "rssi@1m": number | null;
   };

  let device:DeviceSetting|null = null;

   onMount(() => {
      fetch(`/api/device/${deviceId}`)
          .then((response) => {
            if (response.status != 200) throw new Error(response.statusText);
            return response.json();
          })
          .then((d) => device = d)
          .catch((error) => device = {"originalId" : deviceId, "name": null, "rssi@1m": null});
        });

  function save() {
    if (device) {
      fetch(`/api/device/${device.originalId}`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(device),
      })
        .then((response) => {
          if (response.status != 200) throw new Error(response.statusText);
          drawerStore.close();
        })
    }
  }
</script>

{#if device}
<form class="border border-surface-500 p-4 space-y-4">
  <label>
    <span>ID</span>
    <input type="text" disabled bind:value={ device.originalId } />
  </label>
  <label>
    <span>Alias</span>
    <input type="text" bind:value={ device.id } />
  </label>
  <label>
    <span>Name</span>
    <input type="text" bind:value={ device.name } />
  </label>
  <label>
    <span>Rssi@1m</span>
    <input type="text" placeholder="" bind:value={ device["rssi@1m"] } />
  </label>
  <button class="btn btn-filled-primary" on:click={ e => save() }>Save</button>
</form>
{:else}
  <div class="text-center">Loading...</div>
{/if}