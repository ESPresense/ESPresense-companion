<script lang="ts">
	import { resolve } from '$app/paths';
	import { devices, nodes, config, wsManager } from '$lib/stores';
	import Map from '$lib/Map.svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import type { DeviceSetting, NodeSetting } from '$lib/types';
	import type { DeviceMessage } from '$lib/types';
	import { onMount, onDestroy } from 'svelte';

	const toastStore = getToastStore();

	// Changed from 'export let deviceId' to 'export let deviceSettings'
	export let deviceSettings: DeviceSetting;

	let nodeSettings: Record<string, NodeSetting | null> = {};

	// Device state - adjusted to fetch based on deviceId
	let selectedFloorId: string | null = null;
	let calibrationSpot: { x: number; y: number; z?: number } | null = null;
	let calibrationSpotHeight = 1.0; // Default height in meters
	let currentRefRssi: number | null = null; // Will set after fetch

	// Local storage for all device messages (keyed by nodeId)
	let deviceMessages: Record<string, DeviceMessage[]> = {};

	// Function to fetch device settings based on deviceId
	async function fetchDeviceSettings() {
		try {
			const response = await fetch(resolve(`/api/device/${deviceId}`));
			if (response.ok) {
				deviceSettings = await response.json();
				currentRefRssi = deviceSettings?.['rssi@1m'] || null;
			} else {
				const errorData = await response.text();
				toastStore.trigger({ message: `Error fetching device settings: ${errorData || response.statusText}`, background: 'preset-filled-error-500' });
			}
		} catch (error) {
			console.error(`Error fetching settings for device ${deviceId}:`, error);
			toastStore.trigger({ message: 'Error fetching device settings.', background: 'preset-filled-error-500' });
		}
	}

	// Function to fetch node settings
	async function fetchNodeSettings(nodeId: string) {
		try {
			const response = await fetch(resolve(`/api/node/${nodeId}`));
			if (response.ok) {
				const data = await response.json();
				nodeSettings[nodeId] = data.settings;
				// Force reactivity by creating a new object reference
				nodeSettings = { ...nodeSettings };
			}
		} catch (error) {
			console.error(`Error fetching settings for node ${nodeId}:`, error);
		}
	}

	// Update the message handler to store all messages in an array
	function handleDeviceMessage(eventData: { deviceId: string; nodeId: string; data: DeviceMessage }) {
		if (eventData.deviceId === deviceSettings.id) {
			// Initialize array if it doesn't exist
			if (!deviceMessages[eventData.nodeId]) {
				deviceMessages[eventData.nodeId] = [];
			}

			// Add new message to the array
			deviceMessages[eventData.nodeId].push(eventData.data);

			// Limit number of stored messages to prevent memory issues
			if (deviceMessages[eventData.nodeId].length > 20) {
				deviceMessages[eventData.nodeId] = deviceMessages[eventData.nodeId].slice(-20);
			}

			// Force reactivity by creating a new object reference
			deviceMessages = { ...deviceMessages };
		}
	}

	onMount(async () => {
		// Initialize currentRefRssi from deviceSettings
		currentRefRssi = deviceSettings?.['rssi@1m'] || null;
		if (deviceSettings?.id) {
			wsManager.subscribeToEvent('deviceMessage', handleDeviceMessage);
			wsManager.subscribeDeviceMessage(deviceSettings.id);
			console.log('Subscribed to device messages for', deviceSettings.id);
		}
	});

	onDestroy(() => {
		if (deviceSettings?.id) {
			wsManager.unsubscribeFromEvent('deviceMessage', handleDeviceMessage);
			wsManager.sendMessage({
				command: 'unsubscribe',
				type: 'deviceMessage',
				deviceId: deviceSettings.id
			});
			deviceMessages = {};
		}
	});

	// Calibration metrics and related reactive state
	let nodeDistances: { id: string; name: string; distance: number; nodeZ?: number }[] = [];
	let rssiValues: { [key: string]: number | null } = {};
	let includedNodes: { [key: string]: boolean } = {};
	let calculatedRefRssi: number | null = null;

	// Error handling adjusted for fetched settings
	$: if (deviceSettings?.error) {
		toastStore.trigger({ message: deviceSettings.error, background: 'preset-filled-error-500' });
	}

	// Reactive device and floor lookup
	$: device = $devices?.find((d: any) => d.id === deviceSettings.id);
	$: floor = $config?.floors.find((f: any) => f.id === selectedFloorId);
	$: bounds = floor?.bounds;

	// Initialize from device data when available
	$: if ($devices && deviceSettings?.id && !calibrationSpot) {
		const device = $devices?.find((d: any) => d.id === deviceSettings.id);
		if (device) {
			if (device.floor !== null) {
				selectedFloorId = device.floor.id;
			}
			if (device.location?.x != null && device.location?.y != null) {
				calibrationSpot = { x: device.location.x, y: device.location.y };
			}
		}
	}

	// Reset data on floor change
	$: if (selectedFloorId && calibrationSpot) {
		rssiValues = {};
		calculatedRefRssi = null;
	}

	// Update Z coordinate when height changes
	$: if (calibrationSpot && calibrationSpotHeight) {
		const floorLowerZ = bounds ? bounds[0][2] : 0;
		calibrationSpot.z = floorLowerZ + calibrationSpotHeight;
	}

	// Calculate node distances whenever calibration spot or floor changes
	$: nodeDistances = calculateNodeDistances(calibrationSpot, selectedFloorId, $nodes, bounds, calibrationSpotHeight);

	// Set default inclusion for nodes
	$: {
		// Fetch settings for each node
		nodeDistances.forEach((node) => {
			// Set default inclusion
			if (includedNodes[node.id] === undefined) {
				includedNodes[node.id] = true;
			}

			// Fetch node settings if not already fetched
			if (!nodeSettings[node.id]) {
				fetchNodeSettings(node.id);
			}
		});
	}

	// Update RSSI values using all available messages for each node
	$: if (nodeDistances.length > 0) {
		const newRssiValues: { [key: string]: number | null } = {};
		nodeDistances.forEach((node) => {
			if (node.id in deviceMessages && deviceMessages[node.id].length > 0) {
				// Use all messages for this node to calculate average RSSI
				const messages = deviceMessages[node.id];
				const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

				if (validRssiValues.length > 0) {
					// Calculate the average RSSI from all messages
					const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;
					newRssiValues[node.id] = avgRssi;
				}
			}
		});
		if (Object.keys(newRssiValues).length > 0) {
			rssiValues = newRssiValues;
		}
	}

	// Calculate final RSSI using all device messages when enough data is collected
	$: if (Object.values(deviceMessages).some((msgs) => msgs.length >= 5)) {
		calculatedRefRssi = calculateFinalRssi();
	}

	// --- Helper functions ---

	// Calculate standard deviation helper function
	function calculateStdDev(values: number[]): number {
		if (values.length <= 1) return 0;

		const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
		const squaredDiffs = values.map((val) => Math.pow(val - mean, 2));
		const variance = squaredDiffs.reduce((sum, val) => sum + val, 0) / values.length;

		return Math.sqrt(variance);
	}

	function calculateNodeDistances(calibrationSpot: { x: number; y: number; z: number } | null, selectedFloorId: string | null, nodes: any[] | undefined, bounds: any, calibrationSpotHeight: number) {
		if (!nodes || !calibrationSpot || !selectedFloorId) {
			return [];
		}
		return nodes
			.filter((node: any) => {
				return node.floors.includes(selectedFloorId) && node.location.x != null && node.location.y != null;
			})
			.map((node: any) => {
				// Use the relative heights for the z-component of the distance calculation
				const distance = Math.sqrt(Math.pow(node.location.x - calibrationSpot.x, 2) + Math.pow(node.location.y - calibrationSpot.y, 2) + Math.pow(node.location.z - calibrationSpot.z, 2));

				const floorLowerZ = bounds ? bounds[0][2] : 0;
				const nodeHeightFromFloor = node.location.z - floorLowerZ;

				return {
					id: node.id,
					name: node.name || node.id,
					distance,
					nodeZ: nodeHeightFromFloor
				};
			});
	}

	// Update this function to use only parameters from device messages
	function calculateFinalRssi() {
		const refRssiEstimates: Array<{ refRssi: number; weight: number }> = [];

		// Calculate using all device messages
		Object.entries(deviceMessages).forEach(([nodeId, messages]) => {
			// Skip if node isn't included or doesn't have enough messages
			if (!includedNodes[nodeId] || messages.length < 5) return;

			// Find the associated node in nodeDistances
			const node = nodeDistances.find((n) => n.id === nodeId);
			if (!node || node.distance < 0.1) return;

			// Extract valid RSSI values
			const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

			if (validRssiValues.length < 5) return;

			// Calculate average RSSI
			const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;

			// Get the node's absorption value or use default of 2 if not available
			const absorption = nodeSettings[nodeId]?.calibration?.absorption ?? 2;

			// Use the node's absorption value (multiplied by 10 to match the scale)
			// The formula is RSSI@1m = RSSI + 10*n*log10(distance) where n is the path loss exponent
			const refRssi = avgRssi + 10 * absorption * Math.log10(node.distance);

			// Weight by inverse distance (closer nodes get higher weight)
			const weight = 1 / Math.max(1, node.distance);

			refRssiEstimates.push({ refRssi, weight });
		});

		// If no valid estimates, return null
		if (refRssiEstimates.length === 0) return null;

		// Calculate weighted average
		const totalWeight = refRssiEstimates.reduce((sum, est) => sum + est.weight, 0);
		const rawValue = refRssiEstimates.reduce((sum, est) => sum + est.refRssi * est.weight, 0) / totalWeight;

		return Math.round(rawValue);
	}

	function toggleNodeInclusion(nodeId: string) {
		includedNodes[nodeId] = !includedNodes[nodeId];
		includedNodes = { ...includedNodes }; // Trigger reactivity
	}

	$: messageStats = calculateMessageStats(deviceMessages);

	// Get message statistics for display
	function calculateMessageStats(deviceMessages: Record<string, DeviceMessage[]>) {
		const stats: Record<string, { count: number; avgRssi: number | null; minRssi: number | null; maxRssi: number | null; stdDev: number | null }> = {};

		Object.entries(deviceMessages).forEach(([nodeId, messages]) => {
			const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

			if (validRssiValues.length > 0) {
				const sum = validRssiValues.reduce((acc, val) => acc + val, 0);
				stats[nodeId] = {
					count: messages.length,
					avgRssi: sum / validRssiValues.length,
					minRssi: Math.min(...validRssiValues),
					maxRssi: Math.max(...validRssiValues),
					stdDev: validRssiValues.length > 1 ? calculateStdDev(validRssiValues) : null
				};
			} else {
				stats[nodeId] = {
					count: messages.length,
					avgRssi: null,
					minRssi: null,
					maxRssi: null,
					stdDev: null
				};
			}
		});

		return stats;
	}

	async function saveCalibration() {
		if (!calculatedRefRssi) return;
		try {
			const response = await fetch(resolve(`/api/device/${deviceSettings?.originalId || deviceSettings?.id}`), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ ...deviceSettings, 'rssi@1m': calculatedRefRssi })
			});
			if (response.ok) {
				currentRefRssi = calculatedRefRssi;
				toastStore.trigger({
					message: 'Calibration saved successfully!',
					background: 'preset-filled-success-500'
				});
			} else if (response.status === 400) {
				const errorData = await response.json();
				throw new Error(errorData.error || 'Bad request. Please check your input.');
			} else {
				throw new Error('Error saving calibration. Please try again.');
			}
		} catch (e: unknown) {
			const error = e as Error;
			toastStore.trigger({ message: error.message, background: 'preset-filled-error-500' });
		}
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Device Calibration</title>
</svelte:head>


<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-4 space-y-6">
		<header class="flex items-center justify-between">
			<h1 class="text-2xl font-bold text-surface-900-100">Device Calibration</h1>
		</header>

		<div class="card p-4 preset-tonal">
			<header class="font-semibold mb-2">Instructions</header>
			<p class="mb-2">This tool helps calibrate the RSSI@1m value for your device to improve location accuracy.</p>
			<ol class="list-decimal pl-6 mb-2">
				<li>Select a floor from the dropdown</li>
				<li>Place the marker where your device is physically located (drag to position)</li>
				<li>Data is automatically collected as you keep the device stationary</li>
				<li>Compare Map Distance (actual) with Est. Distance (calculated from RSSI)</li>
				<li>When stability is good, review and save the calculated value</li>
			</ol>
			<p class="text-sm font-medium mt-2">The closer Map Distance matches Est. Distance for all nodes, the more accurate your positioning will be.</p>
		</div>

		{#if $config?.floors}
			<div class="grid grid-cols-1 gap-4">
				<div>
					<label class="label font-medium mb-1" for="floor-select">Select Floor</label>
					<select id="floor-select" bind:value={selectedFloorId} class="select w-full">
						{#each $config?.floors as { id, name }}
							<option value={id}>{name}</option>
						{/each}
					</select>
				</div>

				{#if calibrationSpot}
					<div>
						<label class="label font-medium mb-1" for="height-input">Height from Floor (m)</label>
						<div class="input-group input-group-divider grid-cols-[1fr_auto]">
							<input id="height-input" type="number" min="0" max="5" step="0.1" bind:value={calibrationSpotHeight} class="input rounded-r-none" />
							<button type="button" class="btn preset-filled-primary-500 rounded-l-none" onclick={() => (calibrationSpotHeight = calibrationSpotHeight)}>Set</button>
						</div>
					</div>
				{/if}
			</div>
		{/if}

		{#if selectedFloorId}
			<div class="card h-[400px] relative overflow-hidden">
				<Map floorId={selectedFloorId} deviceId={device?.id} exclusive={true} calibrate={true} bind:calibrationSpot />
			</div>
		{/if}

		{#if calibrationSpot}
			<div class="grid grid-cols-1 gap-4 lg:grid-cols-3">
				<div class="card p-4 preset-tonal lg:col-span-2">
					<header class="text-xl font-semibold mb-2">Node Distances and RSSI Values</header>
					<p class="text-sm mb-3">
						<span class="font-semibold">Map Distance:</span> Calculated from node and calibration spot positions in 3D space (X, Y, and Z).<br />
						<span class="font-semibold">Est. Distance:</span> Estimated from RSSI using current calibration settings.<br />
						<span class="font-semibold">Height from Floor:</span> The Z-coordinate (height) of the node from the floor.
					</p>
					<div class="table-container">
						<table class="table table-compact">
							<thead>
								<tr>
									<th>Node</th>
									<th>Height from Floor (m)</th>
									<th>Map Distance (m)</th>
									<th>Est. Distance (m)</th>
									<th>RSSI (dBm)</th>
									<th>Est. RSSI@1m</th>
									<th>Include</th>
								</tr>
							</thead>
							<tbody>
								{#each nodeDistances as node}
									<tr>
										<td>{node.name}</td>
										<td>{node.nodeZ?.toFixed(2) || 'n/a'}</td>
										<td>{node.distance?.toFixed(2) || 'n/a'}</td>
										<td>
											{#if rssiValues[node.id] != null && currentRefRssi != null}
												{#if nodeSettings[node.id]?.calibration?.absorption != null}
													{Math.pow(10, (currentRefRssi - (rssiValues[node.id] || 0)) / (10 * (nodeSettings[node.id]?.calibration?.absorption || 2))).toFixed(2)}
												{:else}
													{Math.pow(10, (currentRefRssi - (rssiValues[node.id] || 0)) / 20).toFixed(2)}
												{/if}
											{:else}
												n/a
											{/if}
										</td>
										<td>{rssiValues[node.id] != null ? rssiValues[node.id]?.toFixed(1) : 'n/a'}</td>
										<td>
											{#if rssiValues[node.id] != null && node.distance != null && node.distance > 0.1}
												{Math.round((rssiValues[node.id] || 0) + 10 * (nodeSettings[node.id]?.calibration?.absorption || 2) * Math.log10(node.distance))}
											{:else}
												n/a
											{/if}
										</td>
										<td>
											<input type="checkbox" checked={includedNodes[node.id] || false} onchange={() => toggleNodeInclusion(node.id)} class="checkbox" />
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>

					<!-- Device Message Statistics Table -->
					<header class="text-xl font-semibold mb-2 mt-6">Device Message Statistics</header>
					<div class="table-container">
						<table class="table table-compact">
							<thead>
								<tr>
									<th>Node</th>
									<th>Messages Count</th>
									<th>Avg RSSI (dBm)</th>
									<th>Min RSSI (dBm)</th>
									<th>Max RSSI (dBm)</th>
									<th>Std Deviation</th>
								</tr>
							</thead>
							<tbody>
								{#each Object.entries(messageStats) as [nodeId, stats]}
									{@const node = nodeDistances.find((n) => n.id === nodeId)}
									<tr>
										<td>{node?.name || nodeId}</td>
										<td>{stats.count}</td>
										<td>{stats.avgRssi != null ? stats.avgRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.minRssi != null ? stats.minRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.maxRssi != null ? stats.maxRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.stdDev != null ? stats.stdDev.toFixed(2) : 'n/a'}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				</div>

				<div class="col-span-1 space-y-4">
					<div class="card p-4 preset-tonal">
						<header class="font-semibold mb-2">Data Collection Status</header>
						<div class="mt-4">
							<div class="flex justify-between mb-1">
								<span>Total Messages:</span>
								<span class="font-medium">{Object.values(deviceMessages).reduce((sum, msgs) => sum + msgs.length, 0)}</span>
							</div>
							<div class="progress h-2">
								<div class="progress-bar bg-primary-500" style="width: {Math.min(100, Object.values(deviceMessages).reduce((sum, msgs) => sum + msgs.length, 0) / 2)}%"></div>
							</div>
						</div>
						<p class="mt-4 text-sm">Keep the device stationary for best results.</p>
					</div>
					<div class="card p-4 preset-tonal">
						<header class="font-semibold mb-4">Calibration Results</header>
						<div class="grid grid-cols-1 gap-4 mb-4">
							<div class="card p-4 preset-tonal">
								<header class="font-semibold mb-2">Current Values</header>
								<p class="text-xl font-bold">
									RSSI@1m: {currentRefRssi != null ? Math.round(currentRefRssi) : 'n/a'} dBm
								</p>
							</div>
							<div class="card p-4 preset-filled-primary-500">
								<header class="font-semibold mb-2">New Values</header>
								<p class="text-xl font-bold">
									RSSI@1m: {calculatedRefRssi != null ? calculatedRefRssi : 'n/a'} dBm
								</p>
							</div>
						</div>
						{#if currentRefRssi != null && calculatedRefRssi != null}
							<div class="card p-4 preset-tonal-warning border border-warning-500 mb-4">
								<p>
									This is a <span class="font-semibold">{Math.abs(calculatedRefRssi - Math.round(currentRefRssi))} dBm</span>
									{calculatedRefRssi > currentRefRssi ? 'increase' : 'decrease'}.
								</p>
								<p>This change will affect how distances are calculated for this device.</p>
							</div>
						{/if}
						<button class="btn btn-lg preset-filled-primary-500 w-full" onclick={saveCalibration} disabled={calculatedRefRssi == null || currentRefRssi === calculatedRefRssi}> Accept New Calibration </button>
					</div>
				</div>
			</div>
	{/if}
	</div>
</div>
