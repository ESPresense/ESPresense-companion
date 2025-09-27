<script lang="ts">
	import DeviceActions from '$lib/DeviceActions.svelte';
	import DeviceActiveId from '$lib/DeviceActiveId.svelte';
	import { devices } from '$lib/stores';
	import type { Device } from '$lib/types';
	import DataTable from '$lib/DataTable.svelte';
	import ago from 's-ago';

	export let onselected: ((device: Device) => void) | undefined = undefined;
	let selected = '';

	function select(d: Device) {
		selected = d?.id ?? '';
		onselected?.(d);
	}

	// Format location coordinates in compact format
	function formatLocation(device: Device): string {
		if (device.location?.x != null && device.location?.y != null) {
			const z = device.location?.z != null ? `, ${device.location.z.toFixed(1)}` : '';
			return `${device.location.x.toFixed(1)}, ${device.location.y.toFixed(1)}${z}`;
		}
		return 'n/a';
	}

	let columns = [
		{
			key: 'activeId',
			title: 'Name',
			renderComponent: { component: DeviceActiveId },
			sortable: true,
			defaultSort: true,
			sortValue: (d: Device) => {
				// Sort by active status first (active=0, inactive=1), then by name/id
				const isActive = d.lastSeen && new Date().getTime() - new Date(d.lastSeen).getTime() < (d.timeout || 30000);
				return `${isActive ? '0' : '1'}_${d.name || d.id}`;
			}
		},
		{ key: 'room', title: 'Room / Floor', value: (d: Device) => d.room?.name ?? d.floor?.name ?? 'n/a', sortable: true },
		{ key: 'location', title: 'Location (X, Y, Z)', value: (d: Device) => formatLocation(d), sortable: false },
		{ key: 'fixes', title: 'Fixes', value: (d: Device) => d.fixes ?? 'n/a', sortable: true },
		{ key: 'scale', title: 'Scale', value: (d: Device) => d.scale?.toFixed(3) ?? 'n/a', sortable: true },
		{ key: 'confidence', title: 'Confidence', value: (d: Device) => d.confidence ?? 'n/a', sortValue: (d: Device) => d.confidence ?? -1, sortable: true },
		{ key: 'lastSeen', title: 'Last Seen', value: (d: Device) => (d.lastSeen ? (ago(new Date(d.lastSeen)) ?? 'n/a') : 'n/a'), sortValue: (d: Device) => (d.lastSeen ? new Date(d.lastSeen) : new Date(0)), sortable: true },
		{ key: 'actions', title: '', renderComponent: { component: DeviceActions } }
	];

</script>

<div>
	{#if $devices}
		<DataTable {columns} classNameTable="table  table-compact" rows={$devices} onclickRow={(event) => select(event.row)} />
	{/if}
</div>

