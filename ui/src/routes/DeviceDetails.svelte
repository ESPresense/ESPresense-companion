<script lang="ts">
  import { onMount } from 'svelte';
  import { base } from '$app/paths';
  import { Accordion, AccordionItem, drawerStore } from '@skeletonlabs/skeleton';
  import { Toast, toastStore } from '@skeletonlabs/skeleton';

  import type { ToastSettings } from '@skeletonlabs/skeleton';

  export let deviceId = '';

  type DeviceSetting = {
    originalId: string;
    id: string | null;
    name: string | null;
    "rssi@1m": number | null;
   };

  let device:DeviceSetting|null = null;
  let details:any;

   onMount(() => {
      fetch(`${base}/api/device/${deviceId}`)
          .then((response) => {
            if (response.status != 200) throw new Error(response.statusText);
            return response.json();
          })
          .then((d) => {
            device = d.settings;
            details = d.details;
            return d;
          })
          .catch((e) => {
            device = {"originalId": deviceId, "id": null, "name": null, "rssi@1m": null};
            console.log(e);
            const t: ToastSettings = {	message: e,	preset: 'error' };
            toastStore.trigger(t);
          });
        });

  function save() {
    if (device) {
      fetch(`${base}/api/device/${device.originalId}`, {
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
      .catch((e) => {
          console.log(e);
          const t: ToastSettings = {	message: e,	preset: 'error' };
          toastStore.trigger(t);
        });
    }
  }
</script>

{#if device}
<span class="text-lg font-medium leading-6 badge badge-filled-primary m-2">{@html device.name ?? device.originalId }</span>
<Accordion>
  <AccordionItem spacing="space-y-4" open>
    <svelte:fragment slot="summary">
      <h3>Settings</h3>
    </svelte:fragment>
    <svelte:fragment slot="content">
      <form class="border border-surface-500 p-4 space-y-4">
        <label class="label">
          <span>ID</span>
          <input class="input" type="text" disabled bind:value={ device.originalId } />
        </label>
        <label>
          <span>Alias</span>
          <input class="input" type="text" bind:value={ device.id } />
        </label>
        <label>
          <span>Name</span>
          <input class="input" type="text" bind:value={ device.name } />
        </label>
        <label>
          <span>Rssi@1m</span>
          <input class="input" type="text" placeholder="" bind:value={ device["rssi@1m"] } />
        </label>
        <button class="btn bg-success-700 text-black" on:click={ e => save() }>Save</button>
      </form>
    </svelte:fragment>
  </AccordionItem>
  <AccordionItem spacing="space-y-4">
    <svelte:fragment slot="summary">
      <h3>Details</h3>
    </svelte:fragment>
    <svelte:fragment slot="content">
      {#if details }
      {#each details as d}
      <label>
        <span>{d.key}</span>
        <input class="input" type="text" disabled bind:value={ d.value } />
      </label>
      {/each}
      {/if}
    </svelte:fragment>
  </AccordionItem>
</Accordion>
{:else}
  <div class="text-center">Loading...</div>
{/if}