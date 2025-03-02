<script lang="ts">
	import { page } from '$app/stores';
	import { devices, nodes, config } from '$lib/stores';
	import Map from '$lib/Map.svelte';
	import { onMount } from 'svelte';
	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';

	const toastStore = getToastStore();

	let deviceId = $page.params.deviceId;
	let selectedFloorId: string | null = null;
	let calibrationSpot: { x: number; y: number; z?: number } | null = null;
	let nodeDistances: { id: string; name: string; distance: number; nodeZ?: number }[] = [];
	let rssiValues: { [key: string]: number | null } = {};
	let includedNodes: { [key: string]: boolean } = {};
	let currentRefRssi: number | null = null;
	let calculatedRefRssi: number | null = null;
	let deviceSettings: any = null;
	let collectedData: any[][] = [];
	let stabilityScore: number = 0;
	let showResults = false;
	let calibrationSpotHeight = 1.0; // Default height in meters

	$: device = $devices?.find((d) => d.id === deviceId);
	$: floor = $config?.floors.find((f) => f.id === selectedFloorId);
	$: bounds = floor?.bounds;

	// Handle floor changes
	$: if (selectedFloorId) {
		// Only reset data on floor change, not on calibration spot movement
		if (calibrationSpot) {
			rssiValues = {};
			collectedData = [];
			calculatedRefRssi = null;
			stabilityScore = 0;

			// Recalculate distances when floor changes
			calculateDistances();
		}
	}

	// Track calibrationSpot x,y,z values explicitly to force reactivity
	$: if (calibrationSpot) {
		calculateDistances();
	}

	onMount(async () => {
		try {
			// Fetch the device data
			const response = await fetch(`/api/device/${deviceId}`);
			if (!response.ok) {
				throw new Error('Error fetching device settings ({response.status})');
			}
			deviceSettings = await response.json();
			currentRefRssi = deviceSettings.refRssi;
		} catch (e: unknown) {
			const error = e as Error;
			const t: ToastSettings = { message: error.message, background: 'variant-filled-error' };
			toastStore.trigger(t);
		}
	});

	// Use reactive statement to initialize from device store once it's available
	$: if ($devices && deviceId && !calibrationSpot) {
		const device = $devices.find((d) => d.id === deviceId);
		if (device) {
			if (device.floor !== null) {
				selectedFloorId = device.floor.id;
			}
			if (device.location?.x !== null && device.location?.y !== null) {
				calibrationSpot = { x: device.location.x, y: device.location.y };
				calculateDistances();
			}
		}
	}

	function calculateDistances() {
		if (!$nodes || !calibrationSpot || !selectedFloorId) {
			nodeDistances = [];
			includedNodes = {};
			return;
		}

		// Get all nodes on the selected floor
		const newNodeDistances = $nodes
			.filter((node) => {
				const isOnFloor = node.floors.includes(selectedFloorId || '');
				return isOnFloor && node.location.x != null && node.location.y != null;
			})
			.map((node) => {
				const nodeZ = node.location.z || 0;
				const spotZ = calibrationSpot!.z || calibrationSpotHeight;

				// Calculate height from floor by subtracting the floor's lower z-bound
				const floorLowerZ = bounds ? bounds[0][2] : 0;
				const nodeHeightFromFloor = nodeZ - floorLowerZ;

				// Calculate 3D distance (x,y,z)
				const distance = Math.sqrt(Math.pow(node.location.x - calibrationSpot!.x, 2) + Math.pow(node.location.y - calibrationSpot!.y, 2) + Math.pow(nodeZ - spotZ, 2));

				return {
					id: node.id,
					name: node.name || node.id,
					distance,
					nodeZ: nodeHeightFromFloor
				};
			});

		// Update nodeDistances with the new values
		nodeDistances = newNodeDistances;

		// Initialize includedNodes for the first time or maintain existing state
		nodeDistances.forEach((node) => {
			if (includedNodes[node.id] === undefined) {
				includedNodes[node.id] = true;
			}
		});
	}

	// Reactive RSSI values fetching - moved from function to reactive statement
	$: if ($devices && nodeDistances && nodeDistances.length > 0) {
		const device = $devices.find((d) => d.id === deviceId);
		if (device && device.nodes) {
			nodeDistances.forEach((node) => {
				const nodeData = device.nodes[node.id];
				rssiValues[node.id] = nodeData?.rssi ?? null;
			});
			updateCalculation();
		}
	}

	function updateCalculation() {
		const includedNodeData = nodeDistances.filter((node) => includedNodes[node.id] && rssiValues[node.id] !== null);
		if (includedNodeData.length === 0) {
			calculatedRefRssi = null;
			return;
		}

		// Calculate refRssi using the standard formula: RSSI@d = refRssi - 10n * log10(d)
		// Rearranged to: refRssi = RSSI@d + 10n * log10(d)
		// Using n = 2 (typical path loss exponent)
		const rssiEstimates = includedNodeData
			.map((node) => {
				const rssi = rssiValues[node.id];
				// Skip very close distances to avoid log10 issues
				if (node.distance < 0.1) return null;

				// Calculate refRssi and weight by distance (closer nodes are more reliable)
				const refRssi = rssi !== null ? rssi + 20 * Math.log10(node.distance) : null;
				const weight = 1 / Math.max(1, node.distance);

				return { refRssi, weight };
			})
			.filter((estimate) => estimate !== null);

		if (rssiEstimates.length === 0) {
			calculatedRefRssi = null;
			return;
		}

		// Calculate weighted average
		const totalWeight = rssiEstimates.reduce((sum, est) => sum + est.weight, 0);
		const rawValue = rssiEstimates.reduce((sum, est) => sum + est.refRssi * est.weight, 0) / totalWeight;
		calculatedRefRssi = Math.round(rawValue);
	}

	function showCalibrationResults() {
		showResults = true;
	}

	function updateStabilityScore() {
		if (collectedData.length < 3) {
			stabilityScore = Math.min(20, collectedData.length * 6);
			return;
		}

		// Get the last few sets of readings
		const recentReadings = collectedData.slice(-10);

		// Calculate variance for each node
		const nodeVariances: { [key: string]: number } = {};

		nodeDistances.forEach((node) => {
			// Get all readings for this node
			const nodeReadings = recentReadings
				.map((reading) => {
					const nodeData = reading.find((r) => r.nodeId === node.id);
					return nodeData?.rssi;
				})
				.filter((rssi) => rssi !== null && rssi !== undefined);

			if (nodeReadings.length < 3) return;

			// Calculate variance
			const avg = nodeReadings.reduce((sum, val) => sum + val, 0) / nodeReadings.length;
			const variance = nodeReadings.reduce((sum, val) => sum + Math.pow(val - avg, 2), 0) / nodeReadings.length;

			nodeVariances[node.id] = variance;
		});

		// Average all variances
		const variances: number[] = Object.values(nodeVariances);
		if (variances.length === 0) return;

		const avgVariance = variances.reduce((sum, val) => sum + val, 0) / variances.length;

		// Convert to a 0-100 score (lower variance = higher score)
		// RSSI variance is typically 0-10, so scale accordingly
		stabilityScore = Math.max(20, Math.min(100, 100 - avgVariance * 8));
	}

	function calculateFinalRssi() {
		if (collectedData.length < 5) {
			return;
		}

		// Use the most recent readings
		const usableData = collectedData.slice(-20);

		// Group by node
		const nodeData: {
			[nodeId: string]: Array<{ rssi: number; distance: number }>;
		} = {};

		usableData.forEach((reading) => {
			reading.forEach((nodeReading) => {
				if (nodeReading.rssi === null) return;

				if (!nodeData[nodeReading.nodeId]) {
					nodeData[nodeReading.nodeId] = [];
				}
				nodeData[nodeReading.nodeId].push({
					rssi: nodeReading.rssi,
					distance: nodeReading.distance
				});
			});
		});

		// Calculate median RSSI for each node
		const refRssiEstimates: Array<{ refRssi: number; weight: number }> = [];

		Object.entries(nodeData).forEach(([nodeId, readings]) => {
			if (!includedNodes[nodeId] || readings.length < 5) return;

			// Sort by RSSI and take median
			const sorted = [...readings].sort((a, b) => a.rssi - b.rssi);
			const median = sorted[Math.floor(sorted.length / 2)];

			// Calculate refRssi
			const distance = median.distance;
			if (distance < 0.1) return; // Skip very close distances

			const refRssi = median.rssi + 20 * Math.log10(distance);
			const weight = 1 / Math.max(1, distance);

			refRssiEstimates.push({ refRssi, weight });
		});

		if (refRssiEstimates.length === 0) return;

		// Calculate weighted average
		const totalWeight = refRssiEstimates.reduce((sum, est) => sum + est.weight, 0);
		const rawValue = refRssiEstimates.reduce((sum, est) => sum + est.refRssi * est.weight, 0) / totalWeight;
		calculatedRefRssi = Math.round(rawValue);
	}

	// Also make nodeDistances reactive
	$: nodeDistancesUpdate = nodeDistances.length > 0 ? JSON.stringify(nodeDistances.map((n) => [n.id, n.distance.toFixed(2)])) : '';

	// Continuously collect data and update calculations whenever devices or nodeDistances change
	$: if ($devices && nodeDistances.length > 0) {
		// Add current RSSI readings to collected data
		if (calibrationSpot) {
			const newReading = nodeDistances.map((node) => ({
				nodeId: node.id,
				rssi: rssiValues[node.id],
				distance: node.distance,
				timestamp: Date.now(),
				spotX: calibrationSpot.x,
				spotY: calibrationSpot.y
			}));

			collectedData.push(newReading);

			// Limit the number of readings we keep to prevent memory issues
			if (collectedData.length > 30) {
				collectedData = collectedData.slice(-30);
			}

			updateStabilityScore();
			calculateFinalRssi();
		}
	}

	// Clean up not needed as we're not using intervals

	function hideResults() {
		showResults = false;
	}

	async function saveCalibration() {
		if (!calculatedRefRssi) return;

		try {
			const response = await fetch(`/api/device/${deviceId}`, {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify({
					...deviceSettings,
					refRssi: calculatedRefRssi
				})
			});

			if (response.ok) {
				currentRefRssi = calculatedRefRssi;
				showResults = false;
				toastStore.trigger({ message: 'Calibration saved successfully!' });
			} else {
				throw new Error('Error saving calibration. Please try again.');
			}
		} catch (e: unknown) {
			const error = e as Error;
			const t: ToastSettings = { message: error.message, background: 'variant-filled-error' };
			toastStore.trigger(t);
		}
	}

	function toggleNodeInclusion(nodeId: string) {
		includedNodes[nodeId] = !includedNodes[nodeId];
		updateCalculation();
		calculateFinalRssi();
	}

	function getStabilityLabel(score: number): string {
		if (score < 30) return 'Poor';
		if (score < 60) return 'Fair';
		if (score < 80) return 'Good';
		return 'Excellent';
	}

	function getColorFromStability(score: number): string {
		if (score < 30) return '#f44336'; // Red
		if (score < 60) return '#ff9800'; // Orange
		if (score < 80) return '#ffeb3b'; // Yellow
		return '#4caf50'; // Green
	}
</script>

<div class="container mx-auto p-4 max-w-7xl">
	<h1 class="h1 mb-4">Calibration for Device {deviceId}</h1>

	<div class="card p-4 mb-6 variant-soft">
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
		<div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
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
						<input
							id="height-input"
							type="number"
							min="0"
							max="5"
							step="0.1"
							bind:value={calibrationSpotHeight}
							on:input={() => {
								if (calibrationSpot) calibrationSpot.z = calibrationSpotHeight;
								calculateDistances();
							}}
							class="input"
						/>
						<button
							class="variant-filled-primary"
							on:click={() => {
								if (calibrationSpot) calibrationSpot.z = calibrationSpotHeight;
								calculateDistances();
							}}>Set</button
						>
					</div>
				</div>
			{/if}
		</div>
	{/if}

	{#if selectedFloorId}
		<div class="card h-[500px] mb-6 relative overflow-hidden">
			<Map floorId={selectedFloorId} deviceId={device?.id} exclusive={true} calibrate={true} bind:calibrationSpot />
		</div>
	{/if}

	{#if calibrationSpot}
		<div class="grid grid-cols-1 lg:grid-cols-12 gap-6">
			<div class="card p-4 col-span-1 lg:col-span-8 variant-soft">
				<header class="text-xl font-semibold mb-2">Node Distances and RSSI Values</header>
				<p class="text-sm mb-3">
					<span class="font-semibold">Map Distance:</span> Calculated from node and calibration spot positions in 3D space (X, Y, and Z).<br />
					<span class="font-semibold">Est. Distance:</span> Estimated from RSSI using current calibration settings.<br />
					<span class="font-semibold">Height from Floor:</span> The Z-coordinate (height) of the node from the floor.
				</p>
				<div class="table-container">
					<table class="table table-hover table-compact">
						<thead>
							<tr>
								<th>Node</th>
								<th>Height from Floor (m)</th>
								<th>Map Distance (m)</th>
								<th>RSSI (dBm)</th>
								<th>Est. Distance (m)</th>
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
									<td>{rssiValues[node.id] != null ? rssiValues[node.id].toFixed(1) : 'n/a'}</td>
									<td>
										{#if rssiValues[node.id] != null && currentRefRssi !== null}
											{Math.pow(10, (currentRefRssi - rssiValues[node.id]) / 20).toFixed(2)}
										{:else}
											n/a
										{/if}
									</td>
									<td>
										{#if rssiValues[node.id] != null && node.distance != null && node.distance > 0.1}
											{Math.round(rssiValues[node.id] + 20 * Math.log10(node.distance))}
										{:else}
											n/a
										{/if}
									</td>
									<td>
										<input type="checkbox" checked={includedNodes[node.id] || false} on:change={() => toggleNodeInclusion(node.id)} class="checkbox" />
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>

			<div class="col-span-1 lg:col-span-4 space-y-6">
				<div class="card p-4 variant-soft">
					<header class="font-semibold mb-2">Data Collection Status</header>

					<div class="mt-4">
						<div class="flex justify-between mb-1">
							<span>Stability:</span>
							<span class="font-medium">{getStabilityLabel(stabilityScore)}</span>
						</div>
						<div class="progress h-2">
							<div class="progress-bar" style="width: {stabilityScore}%; background-color: {getColorFromStability(stabilityScore)}"></div>
						</div>
					</div>

					<div class="mt-4">
						<div class="flex justify-between mb-1">
							<span>Samples Collected:</span>
							<span class="font-medium">{collectedData.length}</span>
						</div>
						<div class="progress h-2">
							<div class="progress-bar bg-primary-500" style="width: {Math.min(100, collectedData.length * 5)}%"></div>
						</div>
					</div>

					<p class="mt-4 text-sm">Keep the device stationary for best results.</p>
				</div>

				<div class="card p-4 variant-soft">
					<header class="font-semibold mb-4">Calibration Results</header>
					<div class="space-y-4">
						<div>
							<div class="text-sm text-surface-700">Current RSSI@1m:</div>
							<div class="text-xl font-semibold">
								{currentRefRssi != null ? Math.round(currentRefRssi) : 'n/a'} dBm
							</div>
						</div>
						<div>
							<div class="text-sm text-surface-700">Calculated RSSI@1m:</div>
							<div class="text-xl font-semibold text-primary-500">
								{calculatedRefRssi != null ? calculatedRefRssi : 'n/a'} dBm
							</div>
						</div>

						<div class="flex gap-2">
							<button class="btn btn-lg variant-filled-primary flex-1 mt-4" on:click={saveCalibration} disabled={calculatedRefRssi == null}> Save Calibration </button>

							<button class="btn btn-lg variant-filled-secondary flex-1 mt-4" on:click={showCalibrationResults} disabled={calculatedRefRssi == null}> Review </button>
						</div>
					</div>
				</div>
			</div>
		</div>
	{/if}

	{#if showResults}
		<div class="modal-backdrop" class:backdrop-blur={showResults}>
			<div class="modal-container">
				<div class="modal-content w-modal card p-6 shadow-xl">
					<header class="text-xl font-bold mb-4">Calibration Results</header>

					<div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
						<div class="card p-4 variant-soft">
							<header class="font-semibold mb-2">Current Values</header>
							<p class="text-xl font-bold">
								refRssi: {currentRefRssi != null ? Math.round(currentRefRssi) : 'n/a'} dBm
							</p>
						</div>
						<div class="card p-4 variant-filled-primary">
							<header class="font-semibold mb-2">New Values</header>
							<p class="text-xl font-bold">
								refRssi: {calculatedRefRssi != null ? calculatedRefRssi : 'n/a'} dBm
							</p>
						</div>
					</div>

					<div class="card p-4 variant-ghost-warning mb-6">
						{#if currentRefRssi != null && calculatedRefRssi != null}
							<p>
								This is a <span class="font-semibold">{Math.abs(calculatedRefRssi - Math.round(currentRefRssi))} dBm</span>
								{calculatedRefRssi > currentRefRssi ? 'increase' : 'decrease'}.
							</p>
						{/if}
						<p>This change will affect how distances are calculated for this device.</p>
					</div>

					<footer class="flex flex-wrap justify-end gap-2">
						<button class="btn variant-filled-primary" on:click={saveCalibration}> Accept New Calibration </button>
						<button class="btn variant-filled-secondary" on:click={hideResults}> Continue Collecting Data </button>
						<button class="btn variant-filled-surface" on:click={() => (showResults = false)}> Cancel </button>
					</footer>
				</div>
			</div>
		</div>
	{/if}
</div>

<!-- Using TailwindCSS and SkeletonUI, we don't need custom styles -->
