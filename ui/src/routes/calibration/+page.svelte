<script lang="ts">
	import { writable } from 'svelte/store';
	import { scaleOrdinal, schemeCategory10 } from "d3";
	import { setContext } from 'svelte';
  import { createDataTableStore,	dataTableHandler,	tableInteraction,	tableA11y } from '@skeletonlabs/skeleton';
  import { config, devices } from '../../lib/stores';
  import type { Node, Device } from '../../lib/types';
  import { base } from '$app/paths';

  function coloring(err:Number) {
    if (err == null) {
      return "";
    }
    if (Math.abs(err) < 1) {
      return "bg-green-500";
    }
    if (Math.abs(err) < 5) {
      return "bg-yellow-500";
    }
    return "bg-red-500";
  }

  async function calibration() {
    const response = await fetch(`${base}/api/state/calibration`);
    return await response.json();
  }

  let cal = null;
  const interval = setInterval(() => {
    calibration().then((data) => {
      cal = data;
    });
  }, 5000);
</script>

<svelte:head>
	<title>ESPresense Companion: Calibration</title>
</svelte:head>

<div class="text-column">
	<h1>Calibration</h1>

  <div class="table-container">
    {#if cal?.matrix }
    <table class="table table-hover">
      <thead>
        <tr>
          <th>Name</th>
          {#each Object.entries(cal.matrix) as [id, n]}
          <th>{@html id}</th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each Object.entries(cal.matrix) as [id1, n1]}
        <tr>
          <td>{@html id1}</td>
          {#each Object.entries(cal.matrix) as [id2, n2]}
            <td class="{coloring(n1[id2]?.err)}" >{@html n1[id2]?.err?.toPrecision(3) ?? ""}</td>
          {/each}
        </tr>
        {/each}
      </tbody>
    </table>
    {/if}
  </div>

  <style>
    .table-container { padding: 10px; }
  </style>
</div>
