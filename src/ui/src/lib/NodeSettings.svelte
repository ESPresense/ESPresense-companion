<script lang="ts">
	import { base } from '$app/paths';
	import { Accordion } from '@skeletonlabs/skeleton-svelte';
	import type { ToastSettings } from '@skeletonlabs/skeleton-svelte';
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
					const t: ToastSettings = { message: e, background: 'preset-filled-error-500' };
					toastStore.trigger(t);
				});
		}
	}
</script>

{#if settings}
	<Accordion>
		<Accordion.Item spacing="space-y-4" open>
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
					<label>
						<span>Absorption</span>
						<input class="input" type="text" placeholder="" bind:value={settings.absorption} />
					</label>
					<label>
						<span>Rx Adj Rssi</span>
						<input class="input" type="text" placeholder="" bind:value={settings.rx_adj_rssi} />
					</label>
					<label>
						<span>Tx Ref Rssi</span>
						<input class="input" type="text" placeholder="" bind:value={settings.tx_ref_rssi} />
					</label>
					<label>
						<span>Max Distance</span>
						<input class="input" type="text" placeholder="" bind:value={settings.max_distance} />
					</label>
					<button class="btn bg-success-700 text-black" on:click={(e) => save()}>Save</button>
				</form>
			</svelte:fragment>
		</Accordion.Item>
	</Accordion>
{:else}
	<div class="text-center">Loading...</div>
{/if}
