<script lang="ts">
	import { writable } from 'svelte/store';
	import { scaleOrdinal, schemeCategory10 } from "d3";
	import { setContext } from 'svelte';

	import NodesTable from '../NodesTable.svelte';
</script>

<svelte:head>
	<title>ESPresense Companion: Nodes</title>
</svelte:head>

<div class="text-column">
	<h1 class="h1">Nodes</h1>

	<NodesTable  />
</div>
