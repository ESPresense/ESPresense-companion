<script lang="ts">
	import { base } from '$app/paths';
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';

	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import type { NodeSetting } from './types';

	export let settings: NodeSetting | null = null;
	const toastStore = getToastStore();

	function save() {
		if (settings) {
			fetch(`${base}/api/node/${settings.id}`, {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify(settings)
			})
				.then((response) => {
					if (response.status != 200) throw new Error(response.statusText);
				})
				.catch((e) => {
					console.log(e);
					const t: ToastSettings = { message: e, background: 'variant-filled-error' };
					toastStore.trigger(t);
				});
		}
	}
</script>

{#if settings}
	<Accordion>
		<AccordionItem spacing="space-y-4" open>
			<svelte:fragment slot="summary">
				<h3 class="h3">Settings</h3>
			</svelte:fragment>
			<svelte:fragment slot="content">
				<form class="border border-surface-500 p-4 space-y-4">
					<label class="label">
						<span>ID</span>
						<input class="input" type="text" disabled bind:value={settings.id} />
					</label>
					<label>
						<span>Name</span>
						<input class="input" type="text" bind:value={settings.name} />
					</label>

					<section class="space-y-4">
						<h4 class="text-xl font-semibold">Updating</h4>
						<label class="label">
							<span>Auto Update</span>
							<input type="checkbox" class="checkbox" bind:checked={settings.updating.autoUpdate} />
						</label>
						<label class="label">
							<span>Include Pre-releases</span>
							<input type="checkbox" class="checkbox" bind:checked={settings.updating.preRelease} />
						</label>
					</section>

					<section class="space-y-4">
						<h4 class="text-xl font-semibold">Counting</h4>
						<label class="label">
							<span>ID Prefixes</span>
							<input type="text" class="input" bind:value={settings.counting.idPrefixes} />
						</label>
						<label class="label">
							<span>Start Counting Distance (m)</span>
							<input type="number" class="input" step="0.01" min="0" bind:value={settings.counting.startCountingDistance} />
						</label>
						<label class="label">
							<span>Stop Counting Distance (m)</span>
							<input type="number" class="input" step="0.01" min="0" bind:value={settings.counting.stopCountingDistance} />
						</label>
					</section>

					<section class="space-y-4">
						<h4 class="text-xl font-semibold">Filtering</h4>
						<label class="label">
							<span>Include IDs</span>
							<input type="text" class="input" bind:value={settings.filtering.includeIds} />
						</label>
						<label class="label">
							<span>Exclude IDs</span>
							<input type="text" class="input" bind:value={settings.filtering.excludeIds} />
						</label>
						<label class="label">
							<span>Max Distance (m)</span>
							<input type="number" class="input" step="0.01" min="0" bind:value={settings.filtering.maxDistance} />
						</label>
					</section>

					<section class="space-y-4">
						<h4 class="text-xl font-semibold">Calibration</h4>
						<label class="label">
							<span>RSSI at 1m</span>
							<input type="number" class="input" bind:value={settings.calibration.rssiAt1m} />
						</label>
						<label class="label">
							<span>Rx Adj RSSI</span>
							<input type="number" class="input" bind:value={settings.calibration.rxAdjRssi} />
						</label>
						<label class="label">
							<span>Absorption</span>
							<input type="number" class="input" step="0.01" bind:value={settings.calibration.absorption} />
						</label>
						<label class="label">
							<span>Tx Ref RSSI</span>
							<input type="number" class="input" bind:value={settings.calibration.txRefRssi} />
						</label>
					</section>

					<button class="btn bg-success-700 text-black" on:click={(e) => save()}>Save</button>
				</form>
			</svelte:fragment>
		</AccordionItem>
	</Accordion>
{:else}
	<div class="text-center">Loading...</div>
{/if}
