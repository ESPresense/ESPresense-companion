<script>
  import { onMount } from 'svelte';
  import { calibrateById } from '$lib/urls'

  export let col;
  export let row;

  // Add a function to handle the "Calibrate" button click
  function handleCalibrate() {
    calibrateById(row.originalId ?? row.id)
  }


</script>

<div>
  <button class="btn btn-sm bg-success-700 text-black" on:click|stopPropagation={handleCalibrate}>Calibrate</button>
</div>