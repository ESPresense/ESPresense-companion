<script lang="ts">
	import NodesTable from '$lib/NodesTable.svelte';
	import { SlideToggle } from '@skeletonlabs/skeleton';
	import { getToastStore, type ToastSettings } from '$lib/toast/toastStore';
	import { base } from '$app/paths';
	import type { NodeSettingDetails } from '$lib/types';
	import TriStateCheckbox from '$lib/TriStateCheckbox.svelte';
	import { onMount } from 'svelte';

	let autoUpdate: boolean | null;
	let prerelease: boolean | null;
	let loading = true;
	let saving = false;
	const toastStore = getToastStore();

	async function saveSettings() {
		try {
			saving = true;
			const settings = {
				updating: {
					autoUpdate: autoUpdate,
					prerelease: prerelease
				}
			};

			const response = await fetch(`${base}/api/node/*`, {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify(settings)
			});

			if (!response.ok) throw new Error('Failed to save settings');

			// Optional: Show success toast
			const t: ToastSettings = {
				message: 'Settings saved successfully',
				background: 'variant-filled-success'
			};
			toastStore.trigger(t);
		} catch (error) {
			console.error(error);
			const message = error instanceof Error ? error.message : 'Unknown error occurred';
			const t: ToastSettings = {
				message,
				background: 'variant-filled-error'
			};
			toastStore.trigger(t);
		} finally {
			saving = false;
		}
	}

	async function loadSettings() {
		try {
			loading = true;
			const response = await fetch(`${base}/api/node/*`);

			if (!response.ok) throw new Error('Failed to load settings');

			const data: NodeSettingDetails = await response.json();
			autoUpdate = data.settings.updating.autoUpdate;
			prerelease = data.settings.updating.prerelease;
		} catch (error) {
			console.error(error);
			const message = error instanceof Error ? error.message : 'Unknown error occurred';
			const t: ToastSettings = {
				message,
				background: 'variant-filled-error'
			};
			toastStore.trigger(t);
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		loadSettings();
	});
</script>

<svelte:head>
	<title>ESPresense Companion: Nodes</title>
</svelte:head>

<div class="container mx-auto p-2">
	<div class="flex justify-between items-center my-2 px-2">
		<h1 class="text-3xl font-bold">Nodes</h1>
		{#if loading}
			<div>Loading settings...</div>
		{:else}
			<div class="flex items-center space-x-4">
				<div class="flex items-center space-x-4">
					<TriStateCheckbox
						id="autoUpdate"
						bind:checked={autoUpdate}
						on:change={saveSettings}
						disabled={saving}
					/>
					<span class="pl">Automatically update</span>

					<TriStateCheckbox
						id="prerelease"
						bind:checked={prerelease}
						on:change={saveSettings}
						disabled={saving}
					/>
					<span class="pl">Include pre-released</span>

					{#if saving}
						<span class="text-sm italic">Saving...</span>
					{/if}
				</div>
			</div>
		{/if}
	</div>

	<NodesTable />
</div>