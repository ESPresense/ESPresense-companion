<script lang="ts">
	import { devices } from '$lib/stores';
	import { gotoCalibration } from '$lib/urls';
	import DataTable from '$lib/DataTable.svelte';
	import DeviceActions from '$lib/DeviceActions.svelte';
	import DeviceActiveId from '$lib/DeviceActiveId.svelte';
	import SlideToggle from '$lib/SlideToggle.svelte';
	import type { Device } from '$lib/types';
	import ago from 's-ago';

	$: filteredDevices =
		$devices?.filter((device) => {
			// Check if device is active based on lastSeen and timeout
			if (device.lastSeen == null) return false;
			const timeout = device.timeout !== null && device.timeout !== undefined ? device.timeout : 30000;
			return new Date().getTime() - new Date(device.lastSeen).getTime() < timeout;
		}) || [];

	$: calibrationStats = {
		total: filteredDevices.length,
		calibrated: filteredDevices.filter((d) => d['rssi@1m'] != null).length,
		needsCalibration: filteredDevices.filter((d) => d['rssi@1m'] == null).length
	};

	function isDeviceActive(device: Device): boolean {
		if (device.lastSeen == null) return false;
		const timeout = device.timeout !== null && device.timeout !== undefined ? device.timeout : 30000;
		return new Date().getTime() - new Date(device.lastSeen).getTime() < timeout;
	}

	function formatRssi(value: number | undefined | null): string {
		if (value == null || value === undefined) return 'n/a';
		return `${value} dBm`;
	}

	function baseRoomFloor(d: Device): string {
		return d.room?.name ?? d.floor?.name ?? 'n/a';
	}

	function displayRoomFloor(d: Device): string {
		const base = baseRoomFloor(d);
		return (d.isAnchored ?? false) ? `${base} 📍` : base;
	}

	const columns = [
		{
			key: 'activeId',
			title: 'Device',
			renderComponent: { component: DeviceActiveId },
			sortable: true,
			defaultSort: true,
			sortValue: (d: Device) => {
				const active = isDeviceActive(d);
				return `${active ? '0' : '1'}_${d.name || d.id}`;
			}
		},
		{
			key: 'room',
			title: 'Room / Floor',
			value: (d: Device) => displayRoomFloor(d),
			sortValue: (d: Device) => baseRoomFloor(d),
			sortable: true
		},
		{
			key: 'rssi@1m',
			title: 'Configured RSSI@1m',
			value: (d: Device) => formatRssi(d['rssi@1m']),
			sortValue: (d: Device) => (d['rssi@1m'] !== null && d['rssi@1m'] !== undefined ? d['rssi@1m'] : -999),
			sortable: true
		},
		{
			key: 'measuredRssi@1m',
			title: 'Measured RSSI@1m',
			value: (d: Device) => formatRssi(d['measuredRssi@1m']),
			sortValue: (d: Device) => (d['measuredRssi@1m'] !== null && d['measuredRssi@1m'] !== undefined ? d['measuredRssi@1m'] : -999),
			sortable: true,
			renderHtml: (d: Device) => {
				const measured = d['measuredRssi@1m'];
				const configured = d['rssi@1m'];
				let color = 'text-surface-600-300';
				let text = formatRssi(measured);
				return `<span class="${color}">${text}</span>`;
			}
		},
		{
			key: 'calibrationStatus',
			title: 'Calibration Status',
			value: (d: Device) => (d['rssi@1m'] == null ? 'Not Calibrated' : 'Calibrated'),
			sortValue: (d: Device) => (d['rssi@1m'] == null ? 0 : 1),
			sortable: true,
			renderHtml: (d: Device) => {
				const calibrated = d['rssi@1m'] != null;
				const color = calibrated ? 'text-success-500' : 'text-error-500';
				return `<span class="${color}">${calibrated ? 'Calibrated' : 'Not Calibrated'}</span>`;
			}
		},
		{
			key: 'lastSeen',
			title: 'Last Seen',
			value: (d: Device) => (d.lastSeen != null ? ago(new Date(d.lastSeen)) : 'n/a'),
			sortValue: (d: Device) => (d.lastSeen != null ? new Date(d.lastSeen) : new Date(0)),
			sortable: true
		},
		{
			key: 'actions',
			title: 'Actions',
			renderComponent: { component: DeviceActions }
		}
	];

	function onRowClick({ row }: { row: Device }) {
		if (isDeviceActive(row)) {
			gotoCalibration(row);
		}
	}
</script>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-2">
		<!-- Calibration Statistics -->
		<div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-6">
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-primary-500">{calibrationStats.total}</div>
				<div class="text-sm text-surface-600-400">Total Devices</div>
			</div>
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-success-500">{calibrationStats.calibrated}</div>
				<div class="text-sm text-surface-600-400">Calibrated</div>
			</div>
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-error-500">{calibrationStats.needsCalibration}</div>
				<div class="text-sm text-surface-600-400">Need Calibration</div>
			</div>
		</div>

		<!-- Device Calibration Table -->
		<div class="card">
			<header class="text-lg font-semibold mb-4">Device Calibration</header>
			{#if filteredDevices.length > 0}
				<DataTable {columns} rows={filteredDevices} classNameTable="table table-compact" onclickRow={onRowClick} />
			{:else}
				<div class="text-center py-8 text-surface-600-400">No active devices found</div>
			{/if}
		</div>
	</div>
</div>
