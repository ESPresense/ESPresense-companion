<script>
  import { onMount } from 'svelte';
  export let deviceId = '';
  let device = {};

   onMount(() => {
      fetch(`/api/device/${deviceId}`)
          .then((response) => response.json())
          .then((d) => device = d);
});
</script>

<form class="border border-surface-500 p-4 space-y-4">
  <label>
    <span>ID</span>
    <input type="text" placeholder="Enter name..." disabled bind:value={ device.id } />
  </label>
  <label>
    <span>Name</span>
    <input type="text" placeholder="" bind:value={ device.name } />
  </label>
  <label>
    <span>Rssi@1m</span>
    <input type="text" placeholder="-65" bind:value={ device.rssiRef } />
  </label>
  <button class="btn btn-filled-primary">Save</button>
</form>