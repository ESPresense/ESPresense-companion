<script lang="ts">
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';
	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import type { NodeSetting } from './types';
	import { saveNodeSettings } from '$lib/node';

	export let settings: NodeSetting | null = null;
	const toastStore = getToastStore();

	async function save() {
		if (settings && settings.id) {
			try {
				await saveNodeSettings(settings.id, settings);
			} catch (e) {
				console.log(e);
				const t: ToastSettings = { message: e instanceof Error ? e.message : String(e), background: 'variant-filled-error' };
				toastStore.trigger(t);
			}
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
					<button class="btn bg-success-700 text-black" on:click|preventDefault={save}>Save</button>
				</form>
			</svelte:fragment>
		</AccordionItem>
	</Accordion>
{:else}
	<div class="text-center">Loading...</div>
{/if}
