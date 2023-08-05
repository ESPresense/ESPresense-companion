<script lang="ts">
	import SvelteTable from 'svelte-table';
	import { nodes } from '$lib/stores';
	import type { Node } from '$lib/types';
	import { createEventDispatcher } from 'svelte';
	import NodeActions from './NodeActions.svelte';

	let columns = [
		{ key: 'id', title: 'ID', value: (d) => d.id, sortable: true },
		{ key: 'name', title: 'Name', value: (d) => d.name, sortable: true },
		{ key: 'telemetry.version', title: 'Version', value: (d) => d.telemetry?.version ?? "n/a", sortable: true },
		{ key: 'actions', title: "", renderComponent: { component: NodeActions }},
	];

	let dispatcher = createEventDispatcher();
	let selected = '';

	function select(n: Node) {
		selected = n?.id ?? '';
		dispatcher('selected', n);
	}

	function onRowClick(e) {
		select(e.detail.row);
	}
</script>

<div class="table-container p-2">
{#if $nodes }
<SvelteTable {columns} rows={$nodes} classNameTable="table table-hover table-compact" on:clickRow={onRowClick} sortBy="id" />
{/if}
</div>

<style>
</style>