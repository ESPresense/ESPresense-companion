<script lang="ts">
	import NodesTable from '$lib/NodesTable.svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import { resolve } from '$app/paths';
	import { gotoDetail } from '$lib/urls';
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

			const response = await fetch(resolve('/api/node/*'), {
				method: 'PUT',
				headers: {
					'Content-Type': 'application/json'
				},
				body: JSON.stringify(settings)
			});

			if (!response.ok) throw new Error('Failed to save settings');

			// Optional: Show success toast
			toastStore.trigger({
				message: 'Settings saved successfully',
				background: 'preset-filled-success-500'
			});
		} catch (error) {
			console.error(error);
			const message = error instanceof Error ? error.message : 'Unknown error occurred';
			toastStore.trigger({
				message,
				background: 'preset-filled-error-500'
			});
		} finally {
			saving = false;
		}
	}

	async function loadSettings() {
		try {
			loading = true;
			const response = await fetch(resolve('/api/node/*'));

			if (!response.ok) throw new Error('Failed to load settings');

			const data: NodeSettingDetails = await response.json();
			autoUpdate = data.settings.updating.autoUpdate;
			prerelease = data.settings.updating.prerelease;
		} catch (error) {
			console.error(error);
			const message = error instanceof Error ? error.message : 'Unknown error occurred';
			toastStore.trigger({
				message,
				background: 'preset-filled-error-500'
			});
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

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-4 space-y-6">
		<header class="flex flex-wrap items-center justify-between gap-4">
			<h1 class="text-2xl font-bold text-surface-900-100">Nodes</h1>
			{#if loading}
				<span class="text-sm italic text-surface-600">Loading update preferences...</span>
			{:else}
				<fieldset class="flex flex-wrap items-center gap-x-6 gap-y-3" aria-label="Node update preferences">
					<legend class="sr-only">Node update preferences</legend>
					<label class="flex items-center gap-2 text-sm text-surface-800-200" for="autoUpdate">
						<TriStateCheckbox id="autoUpdate" bind:checked={autoUpdate} onchange={saveSettings} disabled={saving} />
						<span>Automatically update</span>
					</label>
					<label class="flex items-center gap-2 text-sm text-surface-800-200" for="prerelease">
						<TriStateCheckbox id="prerelease" bind:checked={prerelease} onchange={saveSettings} disabled={saving} />
						<span>Include pre-released</span>
					</label>
					{#if saving}
						<span class="rounded-full border border-surface-400 px-3 py-1 text-xs font-semibold uppercase tracking-wider text-surface-700">Saving...</span>
					{/if}
				</fieldset>
			{/if}
		</header>

		<section>
			<NodesTable onselected={(node) => gotoDetail(node)} />
		</section>
	</div>
</div>
