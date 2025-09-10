<script lang="ts">
	import DeviceActions from '$lib/DeviceActions.svelte';
	import DeviceActiveId from '$lib/DeviceActiveId.svelte';
	import { devices } from '$lib/stores';
	import type { Device } from '$lib/types';
	import { createEventDispatcher } from 'svelte';
	import DataTable from '$lib/DataTable.svelte';
	import ago from 's-ago';

	let dispatcher = createEventDispatcher();
	let selected = '';

	function select(d: Device) {
		selected = d?.id ?? '';
		dispatcher('selected', d);
	}

	let columns = [
		{
			key: 'activeId',
			title: 'Id',
			renderComponent: { component: DeviceActiveId },
			sortable: true,
			defaultSort: true,
			sortValue: (d: Device) => {
				// Sort by active status first (active=0, inactive=1), then by id descending
				const isActive = d.lastSeen && new Date().getTime() - new Date(d.lastSeen).getTime() < (d.timeout || 30000);
				return `${isActive ? '0' : '1'}_${d.id}`;
			}
		},
		{ key: 'name', title: 'Name', value: (d) => d.name ?? '', sortable: true },
		{ key: 'room', title: 'Room / Floor', value: (d) => d.room?.name ?? d.floor?.name ?? 'n/a', sortable: true },
		{ key: 'location.x', title: 'X', value: (d) => d.location?.x, sortable: true },
		{ key: 'location.y', title: 'Y', value: (d) => d.location?.y, sortable: true },
		{ key: 'location.z', title: 'Z', value: (d) => d.location?.z, sortable: true },
		{ key: 'fixes', title: 'Fixes', value: (d) => d.fixes ?? 'n/a', sortable: true },
		{ key: 'scale', title: 'Scale', value: (d) => d.scale?.toFixed(3) ?? 'n/a', sortable: true },
		{ key: 'confidence', title: 'Confidence', value: (d) => d.confidence ?? 'n/a', sortValue: (d: Device) => d.confidence ?? -1, sortable: true },
		{ key: 'lastSeen', title: 'Last Seen', value: (d) => ((d.lastSeen ?? '') == '' ? 'n/a' : (ago(new Date(d.lastSeen)) ?? 'n/a')), sortValue: (d: Device) => (d.lastSeen ? new Date(d.lastSeen) : new Date(0)), sortable: true },
		{ key: 'actions', title: '', renderComponent: { component: DeviceActions } }
	];

	function onRowClick(e) {
		select(e.detail.row);
	}
</script>

<div>
	{#if $devices}
		<DataTable {columns} classNameTable="table  table-compact" rows={$devices} on:clickRow={onRowClick} />
	{/if}
</div>

<style>
</style>
