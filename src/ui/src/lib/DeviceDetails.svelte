<script lang="ts">
	import { base } from '$app/paths';
	import { Accordion, AccordionItem } from '@skeletonlabs/skeleton';
	import { getToastStore } from '@skeletonlabs/skeleton';

	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import type { DeviceSetting } from './types';

	export let settings: DeviceSetting | null = null;
	export let details: any;
	const toastStore = getToastStore();

	function save() {
		if (settings) {
			const rssiRef = parseInt(settings['rssi@1m'] ?? "");
			settings['rssi@1m'] = isNaN(rssiRef) ? null : rssiRef;

			fetch(`${base}/api/device/${settings.originalId}`, {
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
						<input class="input" type="text" disabled bind:value={settings.originalId} />
					</label>
					<label>
						<span>Alias</span>
						<input class="input" type="text" bind:value={settings.id} />
					</label>
					<label>
						<span>Name</span>
						<input class="input" type="text" bind:value={settings.name} />
					</label>
					<label>
						<span>Rssi@1m</span>
						<input class="input" type="text" placeholder="" bind:value={settings['rssi@1m']} />
					</label>
					<button class="btn bg-success-700 text-black" on:click={(e) => save()}>Save</button>
				</form>
			</svelte:fragment>
		</AccordionItem>
	</Accordion>
{:else}
	<div class="text-center">Loading...</div>
{/if}
