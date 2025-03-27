<script lang="ts">
	import { base } from '$app/paths';
	import { devices, nodes, config, wsManager } from '$lib/stores';
	import Map from '$lib/Map.svelte';
	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';
	import type { DeviceSetting, NodeSetting } from '$lib/types';
	import type { DeviceMessage } from '$lib/types';
	import { onMount, onDestroy } from 'svelte';

	const toastStore = getToastStore();

	// Props
	export let deviceId: string;

	// Internal State
	let nodeSettings: Record<string, NodeSetting | null> = {};
	let deviceSettings: DeviceSetting | null = null; // Fetched in onMount
	let selectedFloorId: string | null = null;
	let calibrationSpot: { x: number; y: number; z?: number } | null = null;
	let calibrationSpotHeight = 1.0; // Default height in meters
	let currentRefRssi: number | null = null;
	let isLoading = true;
	let errorMessage: string | null = null;

	// Local storage for all device messages (keyed by nodeId)
	let deviceMessages: Record<string, DeviceMessage[]> = {};

	// --- Lifecycle ---
	onMount(async () => {
		await fetchDeviceSettings();
		if (deviceId && !errorMessage) {
			wsManager.subscribeToEvent('deviceMessage', handleDeviceMessage);
			wsManager.subscribeDeviceMessage(deviceId);
			console.log('Subscribed to device messages for', deviceId);
		}
		isLoading = false;
	});

	onDestroy(() => {
		if (deviceId) {
			wsManager.unsubscribeFromEvent('deviceMessage', handleDeviceMessage);
			wsManager.sendMessage({
				command: 'unsubscribe',
				type: 'deviceMessage',
				deviceId
			});
			deviceMessages = {};
		}
	});

	// --- Data Fetching ---
	async function fetchDeviceSettings() {
		if (!deviceId) {
			errorMessage = 'Device ID is missing.';
			return;
		}
		try {
			const response = await fetch(`${base}/api/device/${deviceId}`);
			if (response.ok) {
				deviceSettings = await response.json();
				currentRefRssi = deviceSettings?.['rssi@1m'] || null;
				// Initialization of floor/spot moved to reactive block below using $devices store
			} else {
				// Handle fetch error response
				const errorData = await response.text();
				errorMessage = `Error fetching device settings: ${response.status} ${errorData || response.statusText}`;
				toastStore.trigger({ message: errorMessage, background: 'variant-filled-error' });
			}
		} catch (error: any) { // Catch network or other errors
			errorMessage = `Error fetching device settings: ${error.message}`;
			console.error(errorMessage, error);
			toastStore.trigger({ message: errorMessage, background: 'variant-filled-error' });
		}
	}

	async function fetchNodeSettings(nodeId: string) {
		// Avoid refetching if already present or fetching
		if (nodeSettings[nodeId] !== undefined) return;
		nodeSettings[nodeId] = null; // Mark as fetching

		try {
			const response = await fetch(`${base}/api/node/${nodeId}`);
			if (response.ok) {
				const data = await response.json();
				nodeSettings[nodeId] = data.settings;
				nodeSettings = { ...nodeSettings }; // Force reactivity
			} else {
				console.error(`Failed to fetch settings for node ${nodeId}: ${response.status}`);
				nodeSettings[nodeId] = null; // Keep null on error
				nodeSettings = { ...nodeSettings };
			}
		} catch (error) {
			console.error(`Error fetching settings for node ${nodeId}:`, error);
			nodeSettings[nodeId] = null; // Keep null on error
			nodeSettings = { ...nodeSettings };
		}
	}

	// --- WebSocket Handler ---
	function handleDeviceMessage(eventData: { deviceId: string; nodeId: string; data: DeviceMessage }) {
		if (eventData.deviceId === deviceId) {
			if (!deviceMessages[eventData.nodeId]) {
				deviceMessages[eventData.nodeId] = [];
			}
			deviceMessages[eventData.nodeId].push(eventData.data);
			if (deviceMessages[eventData.nodeId].length > 20) {
				deviceMessages[eventData.nodeId] = deviceMessages[eventData.nodeId].slice(-20);
			}
			deviceMessages = { ...deviceMessages }; // Force reactivity
		}
	}

	// --- Calibration Metrics & State ---
	let nodeDistances: { id: string; name: string; distance: number; nodeZ?: number }[] = [];
	let rssiValues: { [key: string]: number | null } = {};
	let includedNodes: { [key: string]: boolean } = {};
	let calculatedRefRssi: number | null = null;
	let stabilityScore: number = 0;
	let messageStats: Record<string, { count: number, avgRssi: number | null, minRssi: number | null, maxRssi: number | null, stdDev: number | null }> = {};

	// --- Reactive Calculations ---
	$: floor = $config?.floors.find((f: any) => f.id === selectedFloorId);
	$: bounds = floor?.bounds;

	// Initialize floor/spot from the $devices store once settings are loaded
	$: if (!isLoading && deviceId && $devices && !calibrationSpot && selectedFloorId === null) {
		const device = $devices.find(d => d.id === deviceId);
		if (device) {
			if (device.floor) {
				selectedFloorId = device.floor.id;
			}
			if (device.location?.x != null && device.location?.y != null) {
				calibrationSpot = { x: device.location.x, y: device.location.y };
				// Z will be calculated below
			} else if (selectedFloorId) {
				// Default to center if no location but floor is known
				const floor = $config?.floors.find(f => f.id === selectedFloorId);
				if (floor?.bounds) {
					const centerX = (floor.bounds[0][0] + floor.bounds[1][0]) / 2;
					const centerY = (floor.bounds[0][1] + floor.bounds[1][1]) / 2;
					calibrationSpot = { x: centerX, y: centerY };
				}
			}
		}
	}


	// Reset data on floor change
	$: if (selectedFloorId) {
		// Don't reset calibrationSpot if it came from device settings initially
		// calibrationSpot = null; // Reset spot requires user interaction again
		rssiValues = {};
		calculatedRefRssi = null;
		stabilityScore = 0;
		deviceMessages = {}; // Clear old messages on floor change
		nodeDistances = []; // Recalculate distances
		includedNodes = {}; // Reset inclusions
		nodeSettings = {}; // Refetch node settings for new floor
	}

	// Update Z coordinate when height or bounds change
	$: if (calibrationSpot && calibrationSpotHeight != null && bounds) {
		const floorLowerZ = bounds[0][2]; // Assuming bounds[0] is lower-left-bottom
		calibrationSpot = { ...calibrationSpot, z: floorLowerZ + calibrationSpotHeight };
	} else if (calibrationSpot) {
		// If bounds aren't available yet, maybe set Z later or use a default?
		// For now, let's ensure Z exists even if bounds are missing
		calibrationSpot = { ...calibrationSpot, z: calibrationSpot.z ?? calibrationSpotHeight };
	}

	// Calculate node distances whenever calibration spot (with Z!), floor, nodes, or height changes
	$: nodeDistances = (calibrationSpot?.z != null) ? calculateNodeDistances(calibrationSpot as { x: number; y: number; z: number }, selectedFloorId, $nodes, bounds) : [];

	// Set default inclusion and fetch settings for nodes
	$: {
		nodeDistances.forEach((node) => {
			if (includedNodes[node.id] === undefined) {
				includedNodes[node.id] = true;
			}
			fetchNodeSettings(node.id); // Will only fetch if not already fetched/fetching
		});
		// Clean up includedNodes if a node is no longer in nodeDistances
		Object.keys(includedNodes).forEach(nodeId => {
			if (!nodeDistances.some(n => n.id === nodeId)) {
				delete includedNodes[nodeId];
			}
		});
		includedNodes = {...includedNodes}; // Reactivity
	}

	// Update RSSI values using average of available messages for each node
	$: if (nodeDistances.length > 0) {
		const newRssiValues: { [key: string]: number | null } = {};
		nodeDistances.forEach((node) => {
			const messages = deviceMessages[node.id];
			if (messages && messages.length > 0) {
				const validRssiValues = messages
					.map(msg => msg.rssi)
					.filter(rssi => rssi != null) as number[];

				if (validRssiValues.length > 0) {
					const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;
					newRssiValues[node.id] = avgRssi;
				} else {
					newRssiValues[node.id] = null;
				}
			} else {
				newRssiValues[node.id] = null; // No messages yet
			}
		});
		rssiValues = newRssiValues;
	}

	// Update stability score based on all device messages
	$: stabilityScore = calculateStabilityScore(deviceMessages, includedNodes);

	// Calculate final RSSI using all device messages when enough data is collected
	$: calculatedRefRssi = calculateFinalRssi(deviceMessages, nodeDistances, includedNodes, nodeSettings);

	// Calculate message statistics
	$: messageStats = calculateMessageStats(deviceMessages);


	// --- Helper Functions ---
	function calculateStdDev(values: number[]): number {
		if (values.length <= 1) return 0;
		const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
		const squaredDiffs = values.map(val => Math.pow(val - mean, 2));
		const variance = squaredDiffs.reduce((sum, val) => sum + val, 0) / values.length;
		return Math.sqrt(variance);
	}

	function calculateNodeDistances(
		spot: { x: number; y: number; z: number } | null,
		floorId: string | null,
		nodes: any[] | undefined,
		floorBounds: number[][] | undefined
	): { id: string; name: string; distance: number; nodeZ?: number }[] {
		if (!nodes || !spot || !floorId) return [];

		const floorLowerZ = floorBounds ? floorBounds[0][2] : 0; // Assume bounds[0] is bottom

		return nodes
			.filter((node: any) =>
				node.floors?.includes(floorId) &&
				node.location?.x != null &&
				node.location?.y != null &&
				node.location?.z != null // Ensure node has Z
			)
			.map((node: any) => {
				const distance = Math.sqrt(
					Math.pow(node.location.x - spot.x, 2) +
					Math.pow(node.location.y - spot.y, 2) +
					Math.pow(node.location.z - spot.z, 2) // Use absolute Z for distance
				);
				const nodeHeightFromFloor = node.location.z - floorLowerZ;

				return {
					id: node.id,
					name: node.name || node.id,
					distance,
					nodeZ: nodeHeightFromFloor
				};
			});
	}

	function calculateFinalRssi(
		messagesData: Record<string, DeviceMessage[]>,
		distances: { id: string; distance: number }[],
		included: { [key: string]: boolean },
		settings: Record<string, NodeSetting | null>
	): number | null {
		const refRssiEstimates: Array<{ refRssi: number; weight: number }> = [];

		Object.entries(messagesData).forEach(([nodeId, messages]) => {
			if (!included[nodeId] || messages.length < 5) return;

			const nodeDist = distances.find(n => n.id === nodeId);
			if (!nodeDist || nodeDist.distance < 0.1) return; // Skip if too close or not found

			const validRssiValues = messages
				.map(msg => msg.rssi)
				.filter(rssi => rssi != null) as number[];

			if (validRssiValues.length < 5) return;

			const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;
			const absorption = settings[nodeId]?.calibration?.absorption ?? 2; // Default absorption
			const refRssi = avgRssi + 10 * absorption * Math.log10(nodeDist.distance);
			const weight = 1 / Math.max(1, nodeDist.distance); // Inverse distance weighting

			refRssiEstimates.push({ refRssi, weight });
		});

		if (refRssiEstimates.length === 0) return null;

		const totalWeight = refRssiEstimates.reduce((sum, est) => sum + est.weight, 0);
		if (totalWeight === 0) return null; // Avoid division by zero

		const weightedAvg = refRssiEstimates.reduce((sum, est) => sum + est.refRssi * est.weight, 0) / totalWeight;
		return Math.round(weightedAvg);
	}

	function calculateStabilityScore(
		messagesData: Record<string, DeviceMessage[]>,
		included: { [key: string]: boolean }
	): number {
		const nodeVariances: number[] = [];
		let totalMessages = 0;

		Object.entries(messagesData).forEach(([nodeId, messages]) => {
			totalMessages += messages.length;
			if (!included[nodeId] || messages.length < 3) return;

			const validRssiValues = messages
				.slice(-10) // Use recent messages for variance
				.map(msg => msg.rssi)
				.filter(rssi => rssi != null) as number[];

			if (validRssiValues.length < 3) return;

			const stdDev = calculateStdDev(validRssiValues);
			nodeVariances.push(stdDev * stdDev); // Variance = stdDev^2
		});

		if (nodeVariances.length === 0) {
			// Score based on message count if no variances calculated yet
			return Math.min(20, totalMessages); // Cap at 20 until variance is measurable
		}

		const avgVariance = nodeVariances.reduce((sum, val) => sum + val, 0) / nodeVariances.length;
		// Scale score: Lower variance = higher score. Max 100.
		// Adjust multiplier (e.g., 8) to tune sensitivity.
		return Math.max(0, Math.min(100, 100 - avgVariance * 8));
	}

	function calculateMessageStats(
		messagesData: Record<string, DeviceMessage[]>
	): Record<string, { count: number, avgRssi: number | null, minRssi: number | null, maxRssi: number | null, stdDev: number | null }> {
		const stats: Record<string, { count: number, avgRssi: number | null, minRssi: number | null, maxRssi: number | null, stdDev: number | null }> = {};

		Object.entries(messagesData).forEach(([nodeId, messages]) => {
			const validRssiValues = messages
				.map(msg => msg.rssi)
				.filter(rssi => rssi != null) as number[];

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
				stats[nodeId] = { count: messages.length, avgRssi: null, minRssi: null, maxRssi: null, stdDev: null };
			}
		});
		return stats;
	}

	function toggleNodeInclusion(nodeId: string) {
		includedNodes[nodeId] = !includedNodes[nodeId];
		includedNodes = { ...includedNodes }; // Trigger reactivity
	}

	async function saveCalibration() {
		if (!calculatedRefRssi || !deviceSettings) return;
		try {
			// Use originalId if available (from older configs), otherwise use id
			const idToUpdate = deviceSettings.originalId || deviceId;
			const response = await fetch(`${base}/api/device/${idToUpdate}`, {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ ...deviceSettings, 'rssi@1m': calculatedRefRssi })
			});
			if (response.ok) {
				currentRefRssi = calculatedRefRssi;
				// Update the local deviceSettings state as well
				deviceSettings = { ...deviceSettings, 'rssi@1m': calculatedRefRssi };
				toastStore.trigger({ message: 'Calibration saved successfully!' });
			} else {
				const errorText = await response.text();
				throw new Error(`Error saving calibration: ${response.status} ${errorText || response.statusText}`);
			}
		} catch (e: unknown) {
			const error = e as Error;
			console.error("Save Calibration Error:", error);
			toastStore.trigger({ message: error.message, background: 'variant-filled-error' });
		}
	}

</script>

<div class="container mx-auto p-4 max-w-7xl">
	{#if isLoading}
		<p>Loading device calibration...</p>
	{:else if errorMessage}
		<div class="alert variant-filled-error">
			<strong>Error:</strong> {errorMessage}
		</div>
	{:else}
		<!-- Instructions -->
		<div class="card p-4 mb-6 variant-soft">
			<header class="font-semibold mb-2">Instructions</header>
			<p class="mb-2">This tool helps calibrate the RSSI@1m value for your device to improve location accuracy.</p>
			<ol class="list-decimal pl-6 mb-2">
				<li>Select the floor the device is currently on.</li>
				<li>Place the marker <i class="fa-solid fa-location-crosshairs"></i> where your device is physically located (drag to position).</li>
				<li>Adjust the 'Height from Floor' to match the device's actual height.</li>
				<li>Keep the device stationary. Data is automatically collected via Bluetooth.</li>
				<li>Monitor the 'Node Distances' table: Compare 'Map Distance' (geometric) with 'Est. Distance' (from RSSI).</li>
				<li>Use the checkboxes to include/exclude nodes with inconsistent data.</li>
				<li>When the 'Stability Score' is high and 'Est. Distance' values seem reasonable, review the 'New Values' and click 'Accept New Calibration'.</li>
			</ol>
			<p class="text-sm font-medium mt-2">The closer 'Map Distance' matches 'Est. Distance' for included nodes, the better the calibration.</p>
		</div>

		<!-- Floor and Height Selection -->
		{#if $config?.floors}
			<div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
				<div>
					<label class="label font-medium mb-1" for="floor-select">Select Floor</label>
					<select id="floor-select" bind:value={selectedFloorId} class="select w-full" disabled={!$config?.floors || $config.floors.length === 0}>
						{#if !$config?.floors || $config.floors.length === 0}
							<option disabled selected>No floors configured</option>
						{:else}
							<option value={null} disabled>-- Select a Floor --</option>
							{#each $config.floors as { id, name }}
								<option value={id}>{name}</option>
							{/each}
						{/if}
					</select>
				</div>

				{#if calibrationSpot}
					<div>
						<label class="label font-medium mb-1" for="height-input">Device Height from Floor (m)</label>
						<input
							id="height-input"
							type="number"
							min="0"
							max="10"
							step="0.05"
							bind:value={calibrationSpotHeight}
							class="input w-full"
						/>
					</div>
				{/if}
			</div>
		{/if}

		<!-- Map -->
		{#if selectedFloorId}
			<div class="card h-[500px] mb-6 relative overflow-hidden variant-surface">
				<Map floorId={selectedFloorId} {deviceId} exclusive={true} calibrate={true} bind:calibrationSpot />
			</div>
		{:else if $config?.floors && $config.floors.length > 0}
			<p class="text-center text-gray-500 mb-6">Please select a floor to view the map and begin calibration.</p>
		{/if}

		<!-- Calibration Data & Results -->
		{#if calibrationSpot && selectedFloorId}
			<div class="grid grid-cols-1 lg:grid-cols-12 gap-6">
				<!-- Node Data Table -->
				<div class="card p-4 col-span-1 lg:col-span-8 variant-soft">
					<header class="text-xl font-semibold mb-2">Node Data</header>
					<p class="text-sm mb-3">
						<span class="font-semibold">Map Dist:</span> Geometric distance (3D).<br />
						<span class="font-semibold">Est. Dist:</span> Calculated from RSSI using <strong>current</strong> device calibration.<br />
						<span class="font-semibold">Height:</span> Node's height relative to the selected floor's base Z.<br />
						<span class="font-semibold">Est. RSSI@1m:</span> Calculated per node based on its distance and current RSSI.
					</p>
					<div class="table-container max-h-[400px] overflow-y-auto">
						<table class="table table-hover table-compact w-full">
							<thead>
								<tr>
									<th>Node</th>
									<th>Height (m)</th>
									<th>Map Dist (m)</th>
									<th>Est. Dist (m)</th>
									<th>Avg RSSI (dBm)</th>
									<th>Est. RSSI@1m</th>
									<th>Include</th>
								</tr>
							</thead>
							<tbody>
								{#each nodeDistances as node (node.id)}
									{@const nodeSetting = nodeSettings[node.id]}
									{@const absorption = nodeSetting?.calibration?.absorption ?? 2}
									{@const avgRssi = messageStats[node.id]?.avgRssi}
									{@const estDist = avgRssi != null && currentRefRssi != null ? Math.pow(10, (currentRefRssi - avgRssi) / (10 * absorption)) : null}
									{@const estRefRssi = avgRssi != null && node.distance > 0.1 ? Math.round(avgRssi + 10 * absorption * Math.log10(node.distance)) : null}
									<tr class={includedNodes[node.id] ? '' : 'opacity-50'}>
										<td>{node.name}</td>
										<td>{node.nodeZ?.toFixed(2) ?? 'n/a'}</td>
										<td>{node.distance?.toFixed(2) ?? 'n/a'}</td>
										<td>{estDist?.toFixed(2) ?? 'n/a'}</td>
										<td>{avgRssi?.toFixed(1) ?? 'n/a'}</td>
										<td>{estRefRssi ?? 'n/a'}</td>
										<td>
											<input type="checkbox" bind:checked={includedNodes[node.id]} on:change={() => toggleNodeInclusion(node.id)} class="checkbox" />
										</td>
									</tr>
								{:else}
									<tr><td colspan="7" class="text-center">No nodes found for this floor, or calibration spot not set.</td></tr>
								{/each}
							</tbody>
						</table>
					</div>

					<!-- Device Message Statistics Table -->
					<header class="text-xl font-semibold mb-2 mt-6">Message Statistics (Last 20 per Node)</header>
					<div class="table-container max-h-[300px] overflow-y-auto">
						<table class="table table-hover table-compact w-full">
							<thead>
								<tr>
									<th>Node</th>
									<th>Count</th>
									<th>Avg RSSI</th>
									<th>Min RSSI</th>
									<th>Max RSSI</th>
									<th>Std Dev</th>
								</tr>
							</thead>
							<tbody>
								{#each Object.entries(messageStats) as [nodeId, stats] (nodeId)}
									{@const node = nodeDistances.find(n => n.id === nodeId)}
									<tr class={includedNodes[nodeId] ? '' : 'opacity-50'}>
										<td>{node?.name || nodeId}</td>
										<td>{stats.count}</td>
										<td>{stats.avgRssi?.toFixed(1) ?? 'n/a'}</td>
										<td>{stats.minRssi?.toFixed(1) ?? 'n/a'}</td>
										<td>{stats.maxRssi?.toFixed(1) ?? 'n/a'}</td>
										<td>{stats.stdDev?.toFixed(2) ?? 'n/a'}</td>
									</tr>
								{:else}
									<tr><td colspan="6" class="text-center">Waiting for device messages...</td></tr>
								{/each}
							</tbody>
						</table>
					</div>
				</div>

				<!-- Status & Results Column -->
				<div class="col-span-1 lg:col-span-4 space-y-6">
					<!-- Status Card -->
					<div class="card p-4 variant-soft">
						<header class="font-semibold mb-2">Data Collection Status</header>
						<div class="flex justify-between mb-1">
							<span>Stability Score:</span>
							<span class="font-medium">{stabilityScore.toFixed(0)}%</span>
						</div>
						<progress class="progress h-2 mb-2" value={stabilityScore} max="100"></progress>
						<div class="flex justify-between mb-1 text-sm">
							<span>Total Messages:</span>
							<span class="font-medium">{Object.values(deviceMessages).reduce((sum, msgs) => sum + msgs.length, 0)}</span>
						</div>
						<p class="mt-2 text-xs text-gray-500">Keep device stationary. Higher stability score is better.</p>
					</div>

					<!-- Results Card -->
					<div class="card p-4 variant-soft">
						<header class="font-semibold mb-4">Calibration Results</header>
						<div class="grid grid-cols-2 gap-4 mb-4">
							<div class="text-center">
								<div class="text-xs uppercase text-gray-500">Current</div>
								<div class="text-2xl font-bold">
									{currentRefRssi != null ? Math.round(currentRefRssi) : 'N/A'}
									<span class="text-sm font-normal">dBm</span>
								</div>
							</div>
							<div class="text-center">
								<div class="text-xs uppercase text-primary-500">Calculated</div>
								<div class="text-2xl font-bold text-primary-500">
									{calculatedRefRssi != null ? calculatedRefRssi : 'N/A'}
									<span class="text-sm font-normal">dBm</span>
								</div>
							</div>
						</div>

						{#if currentRefRssi != null && calculatedRefRssi != null && currentRefRssi !== calculatedRefRssi}
							<div class="alert variant-ghost-warning text-sm p-2 mb-4">
								Proposed change: {Math.abs(calculatedRefRssi - currentRefRssi)} dBm
								{calculatedRefRssi > currentRefRssi ? 'increase' : 'decrease'}.
							</div>
						{/if}

						<button
							class="btn btn-lg variant-filled-primary w-full"
							on:click={saveCalibration}
							disabled={calculatedRefRssi == null || currentRefRssi === calculatedRefRssi || stabilityScore < 50}
						>
							{#if calculatedRefRssi != null && currentRefRssi === calculatedRefRssi}
								Calibration Matches Current
							{:else if stabilityScore < 50 && calculatedRefRssi != null}
								Waiting for Stability (>50%)
							{:else if calculatedRefRssi == null}
								Waiting for Data...
							{:else}
								Accept New Calibration
							{/if}
						</button>
						{#if calculatedRefRssi != null && currentRefRssi !== calculatedRefRssi && stabilityScore < 50}
							<p class="text-xs text-center mt-1 text-warning-500">Low stability may result in inaccurate calibration.</p>
						{/if}
					</div>
				</div>
			</div>
		{:else if !$config?.floors || $config.floors.length === 0}
			<!-- Handled by floor selection message -->
		{:else}
			<!-- Message shown when floor is selected but spot isn't placed yet -->
			<p class="text-center text-gray-500">Click and drag on the map to set the device's current location.</p>
		{/if}
	{/if}
</div>
