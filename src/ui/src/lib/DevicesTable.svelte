<script lang="ts">
	import DeviceActions from '$lib/DeviceActions.svelte';
	import { devices } from '$lib/stores';
	import type { Device } from '$lib/types';
	import { createEventDispatcher } from 'svelte';
	import SvelteTable from 'svelte-table';
	import ago from 's-ago';

	let dispatcher = createEventDispatcher();
	let selected = '';

	function select(d: Device) {
		selected = d?.id ?? '';
		dispatcher('selected', d);
	}

	let columns = [
		{ key: 'id', title: 'Id', value: (d) => d.id, sortable: true, defaultSort: true },
		{ key: 'name', title: 'Name', value: (d) => d.name ?? '', sortable: true },
		{ key: 'room', title: 'Room / Floor', value: (d) => d.room?.name ?? d.floor?.name ?? 'n/a', sortable: true },
		{ key: 'location.x', title: 'X', value: (d) => d.location?.x, sortable: true },
		{ key: 'location.y', title: 'Y', value: (d) => d.location?.y, sortable: true },
		{ key: 'location.z', title: 'Z', value: (d) => d.location?.z, sortable: true },
		{ key: 'fixes', title: 'Fixes', value: (d) => d.fixes ?? 'n/a', sortable: true },
		{ key: 'scale', title: 'Scale', value: (d) => d.scale?.toFixed(3) ?? 'n/a', sortable: true },
		{ key: 'confidence', title: 'Confidence', value: (d) => d.confidence ?? 'n/a', sortable: true },
		{ key: 'lastSeen', title: 'Last Seen', value: (d) => ((d.lastSeen ?? '') == '' ? 'n/a' : (ago(new Date(d.lastSeen)) ?? 'n/a')), sortable: true },
		{ key: 'actions', title: '', renderComponent: { component: DeviceActions } }
	];

	function onRowClick(e) {
		select(e.detail.row);
	}
</script>

<div class="p-2">
	{#if $devices}
		<SvelteTable {columns} classNameTable="table table-hover table-compact" rows={$devices} on:clickRow={onRowClick} sortBy="id" />
	{/if}
</div>

<style>
</style>
