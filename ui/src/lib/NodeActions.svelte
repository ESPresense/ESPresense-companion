<script lang="ts">
	import { base } from '$app/paths';
	import link from '$lib/images/link.svg';
	import type { Node } from '$lib/types';
	import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';
	import { updateMethod, flavor, version, artifact, flavorNames } from '$lib/firmware';
	import Firmware from '$lib/modals/Firmware.svelte';

	const modalStore = getModalStore();
	const toastStore = getToastStore();

	async function onRestart(i: Node) {
		try {
			var response = await fetch(`${base}/api/node/${i.id}/restart`, { method: 'POST' });
			if (response.status != 200) throw new Error(response.statusText);
			toastStore.trigger({ message: i.name + ' asked to reboot', background: 'variant-filled-primary' });
		} catch (e) {
			console.log(e);
			toastStore.trigger({ message: e, background: 'variant-filled-error' });
		}
	}

	function onUpdate(i: Node) {
		var f = $flavor;
		if (f == '-') f = i.flavor?.value;
    var fn = $flavorNames?.get(f);
		var body = (function () {
			switch ($updateMethod) {
				case 'release':
					return 'with github version ' + $version;
				case 'artifact':
					return 'with github artifact ' + $artifact;
			}
		})();
		if (fn != null) {
			body = body + ' ' + fn;
		}
		if ($updateMethod != 'self') {
			modalStore.trigger({
				title: 'Update ' + (i.name ?? i.id) + ' Firmware',
				body: body,
				type: 'component',
				component: { ref: Firmware, props: { node: i, updateMethod: $updateMethod, flavor: f, cpu: i.cpu?.value, version: $version, artifact: $artifact } }
			});
		} else {
			if (i) {
				fetch(`${base}/api/node/${i.id}/update`, {
					method: 'POST',
					body: ''
				})
					.then((response) => {
						if (response.status != 200) throw new Error(response.statusText);
						const t: ToastSettings = { message: (i.name ?? i.id) + ' asked to update itself', background: 'variant-filled-primary' };
						toastStore.trigger(t);
					})
					.catch((e) => {
						console.log(e);
						const t: ToastSettings = { message: e, background: 'variant-filled-error' };
						toastStore.trigger(t);
					});
			}
		}
	}

	export let row: Node;
	export let col;
</script>

{#if row.online}
	{#if row.telemetry?.version}
		<button on:click={() => onUpdate(row)} disabled={!($updateMethod == 'self' || ($updateMethod == 'release' && $version) || ($updateMethod == 'artifact' && $artifact))} class="btn btn-sm variant-filled">Update</button>
	{/if}

	{#if row.telemetry}
		<button on:click={() => onRestart(row)} class="btn btn-sm variant-filled">Restart</button>
	{/if}

	{#if row.telemetry?.ip}
		<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm variant-filled">
			<span>Visit</span>
			<span><img class="w-4" src={link} alt="External Link" /></span>
		</a>
	{/if}
{/if}
