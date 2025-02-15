<script lang="ts">
	import NodesTable from '$lib/NodesTable.svelte';
	import { getToastStore, SlideToggle } from '@skeletonlabs/skeleton';
	import type { ToastSettings } from '@skeletonlabs/skeleton';
	import { base } from '$app/paths';
	import type { NodeSettingDetails } from '$lib/types';
	import TriStateCheckbox from '$lib/TriStateCheckbox.svelte';

	let autoUpdate: boolean | null;
	let prerelease: boolean | null;
	const toastStore = getToastStore();

	function saveSettings() {
		const settings = {
			updating: {
				autoUpdate: autoUpdate,
				prerelease: prerelease
			}
		};
		fetch(`${base}/api/node/*`, {
			method: 'PUT',
			headers: {
				'Content-Type': 'application/json'
			},
			body: JSON.stringify( settings )
		})
			.then((response) => {
				if (!response.ok) {
					throw new Error('Failed to save settings');
				}
			})
			.catch((e) => {
				console.log(e);
				const t: ToastSettings = { message: e, background: 'variant-filled-error' };
				toastStore.trigger(t);
			});
	}

	// Load initial settings
	fetch(`${base}/api/node/*`)
		.then((response) => response.json())
		.then((data: NodeSettingDetails) => {
			autoUpdate = data.settings.updating.autoUpdate;
			prerelease = data.settings.updating.prerelease;
		})
		.catch((e) => {
			console.log(e);
			const t: ToastSettings = { message: e, background: 'variant-filled-error' };
			toastStore.trigger(t);
		});
</script>

<svelte:head>
	<title>ESPresense Companion: Nodes</title>
</svelte:head>

<div class="container mx-auto p-2">
	<div class="flex justify-between items-center my-2 px-2">
		<h1 class="text-3xl font-bold">Nodes</h1>
		<div class="flex items-center space-x-4">
			<div class="flex items-center space-x-4">
				<TriStateCheckbox id="autoUpdate" bind:checked={autoUpdate} on:change={saveSettings} /><span class="pl">Automatically update</span>
				<TriStateCheckbox id="prerelease" bind:checked={prerelease} on:change={saveSettings} /><span class="pl">Include pre-released</span>
			</div>
		</div>
	</div>

	<NodesTable />
</div>
