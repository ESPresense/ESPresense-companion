<script lang="ts">
	import { RadioGroup, RadioItem } from '@skeletonlabs/skeleton';
	import { base } from '$app/paths';
	import { popup } from '@skeletonlabs/skeleton';
	import type { PopupSettings } from '@skeletonlabs/skeleton';

	function coloring(percent: number) {
		if (percent == null) {
			return '';
		}
		return (
			'background-color: hsl(' + (240 - Math.min(Math.max(percent * 120, 0), 240)) + ', 50%, 50%)'
		);
	}

	function value(n1: any, data_point: number) {
		if (data_point == 0) {
			return Number(Math.round(n1?.percent * 100)) + '%';
		} else if (data_point == 1) {
			return Number(n1?.err?.toPrecision(3));
		} else if (data_point == 2) {
			return Number(n1?.absorption?.toPrecision(3));
		} else if (data_point == 3) {
			return Number(n1?.rx_adj_rssi?.toPrecision(3));
		} else if (data_point == 4) {
			return Number(n1?.tx_ref_rssi?.toPrecision(3));
		}
	}

	async function calibration() {
		const response = await fetch(`${base}/api/state/calibration`);
		return await response.json();
	}

	let selected = {};

	let cal = {};
	calibration().then((data) => {
		cal = data;
	});
	const interval = setInterval(() => {
		calibration().then((data) => {
			cal = data;
		});
	}, 5000);

	let popupSettings: PopupSettings = {
		event: 'hover',
		target: 'examplePopup'
	};

	let rxColumns: Array<string> = [];
	$: {
		let rx = new Set(Object.keys(cal?.matrix ?? {}));
		Object.entries(cal?.matrix ?? {})
			.flatMap(([key, value]) => Object.keys(value))
			.forEach((key) => rx.add(key));
		rxColumns = new Array(...rx);
	}

	let data_point: number = 0;
</script>

<svelte:head>
	<title>ESPresense Companion: Calibration</title>
</svelte:head>

<div class="text-column">
	<h1 class="h1">Calibration</h1>

	<div class="card variant-filled-secondary p-4" data-popup="examplePopup">
		{#if selected}
			Expected {@html Number(selected?.expected?.toPrecision(3))} - Actual {@html Number(
				selected?.actual?.toPrecision(3)
			)} = Error {@html Number(selected?.err?.toPrecision(3))}
		{:else}
			No beacon Received in last 30 seconds
		{/if}
		<div class="arrow variant-filled-secondary" />
	</div>

	<div class="card m-4">
		{#if cal?.matrix}
			<header>
				<div class="flex justify-center p-4">
					<RadioGroup active="variant-filled-primary" hover="hover:variant-soft-primary">
						<RadioItem bind:group={data_point} name="justify" value={0}>Error %</RadioItem>
						<RadioItem bind:group={data_point} name="justify" value={1}>Error (m)</RadioItem>
						<RadioItem bind:group={data_point} name="justify" value={2}>Absorption</RadioItem>
						<RadioItem bind:group={data_point} name="justify" value={3}>Rx Rssi Adj</RadioItem>
						<RadioItem bind:group={data_point} name="justify" value={4}>Tx Rssi Ref</RadioItem>
					</RadioGroup>
				</div>
			</header>
			<section class="p-4 pt-0">
				<table class="table table-hover">
					<thead>
						<tr>
							<th>Name</th>
							{#each rxColumns as id}
								<th>Rx: {@html id}</th>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each Object.entries(cal.matrix) as [id1, n1]}
							<tr>
								<td>Tx: {@html id1}</td>
								{#each rxColumns as id2}
									{#if n1[id2]}
										<td
											use:popup={popupSettings}
											on:mouseover={() => (selected = n1[id2])}
											on:focus={() => (selected = n1[id2])}
											style={coloring(n1[id2]?.percent)}>{@html value(n1[id2], data_point)}</td
										>
									{:else}
										<td />
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
</div>
