<script lang="ts">
	import SvelteTable from 'svelte-table';
	import { nodes } from '$lib/stores';
	import { updateMethod, flavor, version, artifact } from '$lib/firmware';
	import type { Node } from '$lib/types';
	import { createEventDispatcher } from 'svelte';
	import NodeActions from './NodeActions.svelte';
	import NodeOnline from './NodeOnline.svelte';
	import VersionPicker from './VersionPicker.svelte';

	let columns = [
		{ key: 'id', title: 'ID', value: (d: Node) => d.id, sortable: true },
		{ key: 'active', title: 'Active', renderComponent: { component: NodeOnline } },
		{ key: 'name', title: 'Name', value: (d: Node) => d.name ?? '', sortable: true },
		{ key: 'telemetry.version', title: 'Version', value: (d: Node) => d.telemetry?.version ?? 'n/a', sortable: true },
		{ key: 'cpu.name', title: 'CPU', value: (d: Node) => d.cpu?.name ?? 'n/a', sortable: true },
		{ key: 'flavor.name', title: 'Flavor', value: (d: Node) => d.flavor?.name ?? 'n/a', sortable: true },
		{ key: 'telemetry.ip', title: 'IP', value: (d: Node) => d.telemetry?.ip ?? 'n/a', sortable: true },
		{ key: 'actions', title: '', renderComponent: { component: NodeActions } }
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
	{#if $nodes}
		<VersionPicker bind:updateMethod={$updateMethod} bind:flavor={$flavor} bind:version={$version} bind:artifact={$artifact} />
		<SvelteTable {columns} rows={$nodes} classNameTable="table table-hover table-compact" on:clickRow={onRowClick} sortBy="id" />
	{/if}
</div>

<style>
</style>
