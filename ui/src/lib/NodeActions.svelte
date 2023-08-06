<script lang="ts">
	import { base } from '$app/paths';
	import link from '$lib/images/link.svg';
	import type { Node, Device } from '$lib/types';
	import { toastStore } from '@skeletonlabs/skeleton';

	function onUpdate(i: Node) {
		console.log('update', i);
		if (i) {
			fetch(`${base}/api/node/${i.id}/update`, {
				method: 'POST',
				body: ''
			})
				.then((response) => {
					if (response.status != 200) throw new Error(response.statusText);
					const t: ToastSettings = { message: i.name + ' asked to update itself',	background: 'variant-filled-primary', };
					toastStore.trigger(t);
				})
				.catch((e) => {
					console.log(e);
					const t: ToastSettings = { message: e, background: 'variant-filled-error' };
					toastStore.trigger(t);
				});
		}
	}

	export let row: Node;
	export let col;
</script>

{#if row.telemetry?.version}
	<button on:click={() => onUpdate(row)} class="btn btn-sm variant-filled">Update</button>
{/if}

{#if row.telemetry?.ip}
	<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm variant-filled">
		<span>Visit</span>
		<span><img class="w-4" src={link} alt="External Link" /></span>
	</a>
{/if}
