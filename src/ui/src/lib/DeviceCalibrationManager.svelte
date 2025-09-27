<script lang="ts">
	import { devices } from '$lib/stores';
	import { gotoCalibration } from '$lib/urls';
	import DataTable from '$lib/DataTable.svelte';
	import DeviceActions from '$lib/DeviceActions.svelte';
	import SlideToggle from '$lib/SlideToggle.svelte';
	import type { Device } from '$lib/types';
	import ago from 's-ago';

	let showInactiveDevices = false;

	// Filter devices based on activity status
	$: filteredDevices = $devices?.filter(device => {
		if (showInactiveDevices) return true;

		// Check if device is active based on lastSeen and timeout
		if (device.lastSeen == null) return false;
		const timeout = device.timeout !== null && device.timeout !== undefined ? device.timeout : 30000;
		return new Date().getTime() - new Date(device.lastSeen).getTime() < timeout;
	}) || [];

	$: calibrationStats = {
		total: filteredDevices.length,
		calibrated: filteredDevices.filter(d => d['rssi@1m'] != null).length,
		needsCalibration: filteredDevices.filter(d => d['rssi@1m'] == null).length
	};

	function getCalibrationStatus(device: Device): { status: string; color: string } {
		if (device['rssi@1m'] == null) {
			return { status: 'Not Calibrated', color: 'text-error-500' };
		}
		// 0 dBm is a valid calibration value, so device is calibrated
		return { status: 'Calibrated', color: 'text-success-500' };
	}

	function isDeviceActive(device: Device): boolean {
		if (device.lastSeen == null) return false;
		const timeout = device.timeout !== null && device.timeout !== undefined ? device.timeout : 30000;
		return new Date().getTime() - new Date(device.lastSeen).getTime() < timeout;
	}

	function formatRssi(value: number | undefined | null): string {
		if (value == null || value === undefined) return 'n/a';
		return `${value} dBm`;
	}

	function formatLocation(device: Device): string {
		if (device.location?.x != null && device.location?.y != null) {
			const z = device.location?.z != null ? `, ${device.location.z.toFixed(1)}` : '';
			return `${device.location.x.toFixed(1)}, ${device.location.y.toFixed(1)}${z}`;
		}
		return 'n/a';
	}

	const columns = [
		{
			key: 'name',
			title: 'Device',
			value: (d: Device) => d.name || d.id,
			sortable: true,
			defaultSort: true
		},
		{
			key: 'status',
			title: 'Status',
			value: (d: Device) => isDeviceActive(d) ? 'Active' : 'Inactive',
			sortable: true,
			renderHtml: (d: Device) => {
				const active = isDeviceActive(d);
				const color = active ? 'text-success-500' : 'text-surface-500';
				return `<span class="${color}">${active ? 'Active' : 'Inactive'}</span>`;
			}
		},
		{
			key: 'rssi@1m',
			title: 'Configured RSSI@1m',
			value: (d: Device) => formatRssi(d['rssi@1m']),
			sortValue: (d: Device) => d['rssi@1m'] !== null && d['rssi@1m'] !== undefined ? d['rssi@1m'] : -999,
			sortable: true
		},
		{
			key: 'measuredRssi@1m',
			title: 'Measured RSSI@1m',
			value: (d: Device) => formatRssi(d['measuredRssi@1m']),
			sortValue: (d: Device) => d['measuredRssi@1m'] !== null && d['measuredRssi@1m'] !== undefined ? d['measuredRssi@1m'] : -999,
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
			value: (d: Device) => getCalibrationStatus(d).status,
			sortValue: (d: Device) => getCalibrationStatus(d).status === 'Calibrated' ? 1 : 0,
			sortable: true,
			renderHtml: (d: Device) => {
				const { status, color } = getCalibrationStatus(d);
				return `<span class="${color}">${status}</span>`;
			}
		},
		{
			key: 'location',
			title: 'Location (X, Y, Z)',
			value: (d: Device) => formatLocation(d),
			sortable: false
		},
		{
			key: 'lastSeen',
			title: 'Last Seen',
			value: (d: Device) => d.lastSeen != null ? ago(new Date(d.lastSeen)) : 'n/a',
			sortValue: (d: Device) => d.lastSeen != null ? new Date(d.lastSeen) : new Date(0),
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

		<!-- Controls -->
		<div class="flex justify-between items-center mb-4">
			<div class="flex items-center space-x-4">
				<SlideToggle name="show-inactive" bind:checked={showInactiveDevices}>
					<span>Show Inactive Devices</span>
				</SlideToggle>
			</div>
		</div>

		<!-- Device Calibration Table -->
		<div class="card p-4">
			<header class="text-lg font-semibold mb-4">Device Calibration Status</header>
			{#if filteredDevices.length > 0}
				<DataTable
					{columns}
					rows={filteredDevices}
					classNameTable="table table-compact"
					onclickRow={onRowClick}
				/>
			{:else}
				<div class="text-center py-8 text-surface-600-400">
					{showInactiveDevices ? 'No devices found' : 'No active devices found'}
				</div>
			{/if}
		</div>

		<!-- Instructions -->
		<div class="card p-4 mt-4 preset-tonal">
			<header class="font-semibold mb-2">Instructions</header>
			<ul class="list-disc pl-6 space-y-1 text-sm">
				<li>Click "Calibrate" or click on an active device row to start calibration</li>
				<li>Devices marked "Not Calibrated" should be calibrated for optimal accuracy</li>
				<li>Only active devices (recently seen) can be calibrated</li>
			</ul>
		</div>
	</div>
</div>
