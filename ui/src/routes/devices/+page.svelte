<script lang="ts">
	import { writable } from 'svelte/store';
	import { scaleOrdinal, schemeCategory10 } from "d3";
	import { setContext } from 'svelte';

	import DevicesTable from '../DevicesTable.svelte';
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="text-column">
	<h1>Devices</h1>

	<DevicesTable  />
</div>
