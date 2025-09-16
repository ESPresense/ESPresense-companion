<script lang="ts">
	import DataTable from '$lib/DataTable.svelte';
	import { nodes } from '$lib/stores';
	import { updateMethod, firmwareSource, flavor, version, artifact } from '$lib/firmware';
	import type { Node } from '$lib/types';
	import NodeActions from './NodeActions.svelte';
	import NodeActiveId from './NodeActiveId.svelte';
	import VersionPicker from './VersionPicker.svelte';

	export let onselected: ((node: Node) => void) | undefined = undefined;

	let columns = [
		{
			key: 'activeId',
			title: 'Name',
			renderComponent: { component: NodeActiveId },
			sortable: true,
			defaultSort: true,
			sortValue: (d: Node) => {
				// Sort by active status first (online=0, offline=1), then by name/id
				return `${d.online ? '0' : '1'}_${d.name || d.id}`;
			}
		},
		{ key: 'telemetry.version', title: 'Version', value: (d: Node) => d.telemetry?.version ?? 'n/a', sortable: true },
		{ key: 'cpu.name', title: 'CPU', value: (d: Node) => d.cpu?.name ?? 'n/a', sortable: true },
		{ key: 'flavor.name', title: 'Flavor', value: (d: Node) => d.flavor?.name ?? 'n/a', sortable: true },
		{ key: 'telemetry.ip', title: 'IP', value: (d: Node) => d.telemetry?.ip ?? 'n/a', sortable: true },
		{ key: 'actions', title: '', renderComponent: { component: NodeActions } }
	];

	let selected = '';

	function select(n: Node) {
		selected = n?.id ?? '';
		onselected?.(n);
	}

	function onRowClick(e: any) {
		select(e.row);
	}
</script>

<div>
	{#if $nodes}
		<VersionPicker bind:updateMethod={$updateMethod} bind:firmwareSource={$firmwareSource} bind:flavor={$flavor} bind:version={$version} bind:artifact={$artifact} />
		<DataTable {columns} rows={$nodes} classNameTable="table  table-compact" onclickRow={onRowClick} />
	{/if}
</div>

<style>
</style>
