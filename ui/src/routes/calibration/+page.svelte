<script lang="ts">
	import { writable } from 'svelte/store';
	import { scaleOrdinal, schemeCategory10 } from 'd3';
	import { setContext } from 'svelte';
	import {
		createDataTableStore,
		dataTableHandler,
		tableInteraction,
		tableA11y
	} from '@skeletonlabs/skeleton';
	import { config, devices } from '../../lib/stores';
	import type { Node, Device } from '../../lib/types';
	import { base } from '$app/paths';
  import { popup } from '@skeletonlabs/skeleton';
  import type { PopupSettings } from '@skeletonlabs/skeleton';

	function coloring(err: Number) {
		if (err == null) {
			return '';
		}
		if (Math.abs(err) < 1) {
			return 'bg-green-500';
		}
		if (Math.abs(err) < 5) {
			return 'bg-yellow-500';
		}
		return 'bg-red-500';
	}

	async function calibration() {
		const response = await fetch(`${base}/api/state/calibration`);
		return await response.json();
	}

  let selected = {};

	let cal = { };
	calibration().then((data) => {
			cal = data;
		});
	const interval = setInterval(() => {
		calibration().then((data) => {
			cal = data;
		});
	}, 5000);

	let popupSettings: PopupSettings = {
		event: 'hover',
		target: 'examplePopup'
	};

  let rxColumns:Array<string> = [];
  $: {
    let rx = new Set(Object.keys(cal?.matrix ?? {}));
    Object.entries(cal?.matrix ?? {}).flatMap(([key, value]) => Object.keys(value)).forEach((key) => rx.add(key));
    rxColumns = new Array(...rx);
  }
</script>

<svelte:head>
	<title>ESPresense Companion: Calibration</title>
</svelte:head>

<div class="text-column">
	<h1>Calibration</h1>

	<div class="card variant-filled-secondary p-4" data-popup="examplePopup">
  {#if selected}
		Map Distance {@html Number(selected?.map_dist?.toPrecision(3))} - Measured {@html Number(selected?.dist?.toPrecision(3))} = Error {@html Number(selected?.err?.toPrecision(3))}
  {:else}
    	No beacon Received in last 30 seconds
	{/if}
    <div class="arrow variant-filled-secondary" />
	</div>

	<div class="table-container">
		{#if cal?.matrix}
			<table class="table table-hover">
				<thead>
					<tr>
						<th>Name</th>
						{#each rxColumns as id}
							<th>Rx: {@html id}</th>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each Object.entries(cal.matrix) as [id1, n1]}
						<tr>
							<td>Tx: {@html id1}</td>
							{#each rxColumns as id2}
								{#if n1[id2]}
								<td use:popup={popupSettings} on:mouseover="{()=> selected = n1[id2]}" class={coloring(n1[id2]?.err)}
									>{@html Number(n1[id2]?.err?.toPrecision(3)) ?? ''}</td
								>
								{:else}
								<td></td>
								{/if}
							{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		{:else}
			<p>Loading...</p>
		{/if}
	</div>

	<style>
		.table-container {
			padding: 10px;
		}
	</style>
</div>
