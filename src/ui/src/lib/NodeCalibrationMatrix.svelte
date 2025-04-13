<script lang="ts">
	import { calibration } from '$lib/stores';
	import { RadioGroup, RadioItem } from '@skeletonlabs/skeleton';
	import { popup } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';
	import { getModalStore } from '@skeletonlabs/skeleton';
	import { base } from '$app/paths';

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
		Object.keys(matrix).forEach((key) => rxSet.add(key));
		Object.values(matrix).forEach((n1) => {
			Object.keys(n1).forEach((key) => rxSet.add(key));
		});
		rxColumns = Array.from(rxSet);
	}

	let data_point: DataPoint = 0;

	const toastStore = getToastStore();
	const modalStore = getModalStore();

	async function resetCalibration() {
		const confirmed = await new Promise((resolve) => {
			modalStore.trigger({
				type: 'confirm',
				title: 'Reset Calibration',
				body: 'Are you sure you want to reset the calibration? This will reset rx_adj_rssi, tx_ref_rssi, and absorption for all nodes. This action cannot be undone.',
				response: (r: boolean) => resolve(r)
			});
		});

		if (!confirmed) return;

		try {
			const response = await fetch(`${base}/api/state/calibration/reset`, { method: 'POST' });
			if (response.ok) {
				toastStore.trigger({
					message: 'Calibration reset successfully',
					background: 'variant-filled-success'
				});
			} else {
				const errorText = await response.text();
				throw new Error(`Server error ${response.status}: ${errorText}`);
			}
		} catch (error: any) {
			console.error('Error resetting calibration:', error);
			toastStore.trigger({
				message: `Failed to reset calibration: ${error.message}`,
				background: 'variant-filled-error'
			});
		}
	}
</script>

{#if $calibration?.matrix}
	{#each Object.entries($calibration?.matrix) as [id1, n1] (id1)}
		{#each rxColumns as id2 (id2)}
			<div class="card variant-filled-secondary p-4" data-popup={'popup-' + id1 + '-' + id2}>
				{#if n1[id2]}
					Map Distance {Number(n1[id2].mapDistance?.toPrecision(3))} - Measured {Number(n1[id2]?.distance?.toPrecision(3))} = Error {Number(n1[id2]?.diff?.toPrecision(3))}
				{:else}
					No beacon Received in last 30 seconds
				{/if}
				<div class="arrow variant-filled-secondary"></div>
			</div>
		{/each}
	{/each}
{/if}

<div class="card p-2">
	{#if $calibration?.matrix}
		<header>
			<div class="flex justify-between items-center p-2">
				<RadioGroup active="variant-filled-primary" hover="hover:variant-soft-primary">
					<RadioItem bind:group={data_point} name="justify" value={0}>Error %</RadioItem>
					<RadioItem bind:group={data_point} name="justify" value={1}>Error (m)</RadioItem>
					<RadioItem bind:group={data_point} name="justify" value={2}>Absorption</RadioItem>
					<RadioItem bind:group={data_point} name="justify" value={3}>Rx Rssi Adj</RadioItem>
					<RadioItem bind:group={data_point} name="justify" value={4}>Tx Rssi Ref</RadioItem>
					<RadioItem bind:group={data_point} name="justify" value={5}>Variance (m)</RadioItem>
				</RadioGroup>
				<button class="btn variant-filled-warning" on:click={resetCalibration}> Reset Calibration </button>
			</div>
			<div class="flex gap-8 items-center m-4 mt-0">
				<span class="font-semibold">RMSE:</span> <span>{$calibration?.rmse?.toFixed(3) ?? 'n/a'}</span>
				<span class="font-semibold">R:</span> <span>{$calibration?.r?.toFixed(3) ?? 'n/a'}</span>
			</div>
		</header>
		<section class="p-4 pt-0">
			<table class="table table-hover">
				<thead>
					<tr>
						<th>Name</th>
						{#each rxColumns as id}
							<th>Rx: {id}</th>
						{/each}
					</tr>
				</thead>
				<tbody>
					{#each Object.entries($calibration.matrix) as [id1, n1] (id1)}
						<tr>
							<td>Tx: {id1}</td>
							{#each rxColumns as id2 (id2)}
								{#if n1[id2]}
									<td use:popup={{ event: 'hover', target: 'popup-' + id1 + '-' + id2, placement: 'top' }} style={coloring(n1[id2]?.percent)}>{value(n1[id2], data_point)}</td>
								{:else}
									<td></td>
								{/if}
							{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		</section>
	{:else}
		<p>Loading...</p>
	{/if}
</div>
