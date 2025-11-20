<script lang="ts">
	import { calibration } from '$lib/stores';
	import { resolve } from '$app/paths';
	import { getToastStore } from '$lib/toast/toastStore';
	import { showConfirm } from '$lib/modal/modalStore';
	import { tooltip } from '$lib/tooltip';

	enum DataPoint {
		ErrorPercent = 0,
		ErrorMeters = 1,
		Absorption = 2,
		RxRssiAdj = 3,
		TxRssiRef = 4,
		VarianceMeters = 5
	}

	function coloring(percent: number | null): string {
		if (percent == null) {
			return '';
		}
		// Clamp percent between -2 and +2
		percent = Math.max(-2, Math.min(percent, 2));

		// Clamp the color mapping between -1 and +1 for hue purposes
		const colorPercent = Math.max(-1, Math.min(percent, 1));

		// Map colorPercent (-1 to +1) to hue (240° blue -> 120° green -> 0° red)
		const hue = 240 - (colorPercent + 1) * (240 / 2); // Shift from 240° to 0°
		return `background-color: hsl(${hue}, 50%, 50%)`;
	}

	function value(n1: any, data_point: number) {
		if (data_point === DataPoint.ErrorPercent) {
			return n1 ? Number(Math.round(n1.percent * 100)) + '%' : null;
		} else if (data_point >= 1 && data_point <= 5) {
			let num;
			switch (data_point) {
				case DataPoint.ErrorMeters:
					num = n1?.diff;
					break;
				case DataPoint.Absorption:
					num = n1?.absorption;
					break;
				case DataPoint.RxRssiAdj:
					num = n1?.rx_adj_rssi;
					break;
				case DataPoint.TxRssiRef:
					num = n1?.tx_ref_rssi;
					break;
				case DataPoint.VarianceMeters:
					num = n1?.var;
					break;
			}
			return num !== null && num !== undefined ? Number(num.toPrecision(3)) : 'n/a';
		}
	}

	let rxColumns: Array<string> = [];
	$: {
		const matrix = $calibration?.matrix ?? {};
		const rxSet = new Set<string>();

		// Only include receivers that have actual data from at least one transmitter
		Object.values(matrix).forEach((n1) => {
			Object.keys(n1).forEach((key) => {
				// Only add receivers that have measurement data (not empty/null)
				if (n1[key] && Object.keys(n1[key]).length > 0) {
					rxSet.add(key);
				}
			});
		});
		rxColumns = Array.from(rxSet).sort();
	}

	// Helper function to check if a transmitter is an anchored device
	function isAnchored(txName: string): boolean {
		return $calibration?.anchored?.includes(txName) ?? false;
	}

	let data_point: DataPoint = 0;

	const toastStore = getToastStore();

	async function resetCalibration() {
		const confirmed = await showConfirm({
			title: 'Reset Calibration',
			body: 'Are you sure you want to reset the calibration? This will reset rx_adj_rssi, tx_ref_rssi, and absorption for all nodes. This action cannot be undone.'
		});

		if (!confirmed) return;

		try {
			const response = await fetch(resolve('/api/state/calibration/reset'), { method: 'POST' });
			if (response.ok) {
				toastStore.trigger({
					message: 'Calibration reset successfully',
					background: 'preset-filled-success-500'
				});
			} else {
				const errorText = await response.text();
				throw new Error(`Server error ${response.status}: ${errorText}`);
			}
		} catch (error: any) {
			console.error('Error resetting calibration:', error);
			toastStore.trigger({
				message: `Failed to reset calibration: ${error.message}`,
				background: 'preset-filled-error-500'
			});
		}
	}
</script>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-2">
		<!-- Active Optimizers -->
		{#if $calibration?.optimizerState?.optimizers}
		<div class="mb-4">
			<span class="text-sm font-semibold text-surface-600-400">Active Optimizers:</span>
			<span class="text-surface-900-100 ml-2">{$calibration.optimizerState.optimizers}</span>
		</div>
		{/if}

		<!-- Calibration Statistics -->
		{#if $calibration?.optimizerState}
		<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-primary-500">{$calibration?.rmse?.toFixed(3) ?? 'n/a'}</div>
				<div class="text-sm text-surface-600-400">RMSE</div>
			</div>
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-primary-500">{$calibration?.r?.toFixed(3) ?? 'n/a'}</div>
				<div class="text-sm text-surface-600-400">R</div>
			</div>
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-success-500">{$calibration?.optimizerState?.bestRMSE?.toFixed(3) ?? 'n/a'}</div>
				<div class="text-sm text-surface-600-400">Best RMSE</div>
			</div>
			<div class="card p-4 preset-tonal">
				<div class="text-2xl font-bold text-success-500">{$calibration?.optimizerState?.bestR?.toFixed(3) ?? 'n/a'}</div>
				<div class="text-sm text-surface-600-400">Best R</div>
			</div>
		</div>
		{/if}

		{#if $calibration?.matrix}
		<div class="card">
			<header class="text-lg font-semibold mb-4">Node Calibration</header>
			<div class="space-y-4">
				<div class="flex flex-col gap-6 rounded-lg border border-surface-300-700 bg-surface-50-950 p-4 shadow-sm">
					<div class="flex flex-wrap items-center justify-between gap-3">
						<div class="flex flex-wrap items-center gap-2">
							<button class="btn {data_point === 0 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 0)}>Error %</button>
							<button class="btn {data_point === 1 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 1)}>Error (m)</button>
							<button class="btn {data_point === 2 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 2)}>Absorption</button>
							<button class="btn {data_point === 3 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 3)}>Rx Rssi Adj</button>
							<button class="btn {data_point === 4 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 4)}>Tx Rssi Ref</button>
							<button class="btn {data_point === 5 ? 'preset-filled-primary-500' : 'preset-ghost-surface-500'}" onclick={() => (data_point = 5)}>Variance (m)</button>
						</div>
						<button class="btn preset-filled-warning-500" onclick={resetCalibration}> Reset Calibration </button>
					</div>
				</div>
				<div class="overflow-x-auto">
					<table class="table">
						<thead>
							<tr>
								<th style="text-align: center; color: oklch(1 0 none);">Name</th>
								{#each rxColumns as id}
									<th class="h-32 whitespace-nowrap px-2 py-1 min-w-10" style="position: relative;">
										<div style="writing-mode: vertical-rl; text-orientation: mixed; transform: rotate(180deg); position: absolute; bottom: 8px; left: 50%; transform-origin: center; transform: translateX(-50%) rotate(180deg); white-space: nowrap; color: oklch(1 0 none);">Rx: {id}</div>
									</th>
								{/each}
							</tr>
						</thead>
						<tbody>
							{#each Object.entries($calibration.matrix).sort((a, b) => a[0].localeCompare(b[0])) as [id1, n1] (id1)}
								<tr>
									<td style="text-align: right; white-space: nowrap;">Tx: {id1}{#if isAnchored(id1)} 📍{/if}</td>
									{#each rxColumns as id2 (id2)}
										<td style="text-align: center; {coloring(n1[id2]?.percent)}" use:tooltip={n1[id2] ? `Map Distance ${Number(n1[id2].mapDistance?.toPrecision(3))} - Measured ${Number(n1[id2]?.distance?.toPrecision(3))} = Error ${Number(n1[id2]?.diff?.toPrecision(3))}` : 'No beacon Received in last 30 seconds'}
											>{#if n1[id2]}{value(n1[id2], data_point)}{/if}</td
										>
									{/each}
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>
		</div>
		{:else}
		<div class="text-center py-8 text-surface-600-400">
			Loading calibration data...
		</div>
		{/if}
	</div>
</div>

