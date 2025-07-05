<script lang="ts">
	import { Progress } from '@skeletonlabs/skeleton-svelte';
	import { getToastStore } from '$lib/utils/skeleton';
	import { firmwareTypes, cpuNames, getFirmwareUrl, firmwareUpdate } from '$lib/firmware';
	import type { Node } from '$lib/types';

	export let firmwareSource: string;
	export let node: Node;
	export let flavor: string;
	export let cpu: string;
	export let version: string;
	export let artifact: string;
	export let parent: any;

	enum UpdateProgress {
		Form,
		Updating,
		Failed,
		Success
	}

	const toastStore = getToastStore();

	function extractNonNumeric(str: string): string {
		return str.replace(/\d+/g, '');
	}

	let progress: UpdateProgress = UpdateProgress.Form;
	let percentComplete: number = 0;
	let firmware: string;
	let url: string;
	let log: string[] = [];
	let lastNonNumericLog: string | null = null;
	$: url = getFirmwareUrl(firmwareSource, version, artifact, firmware) ?? '#ERR';

	async function onFormSubmit(): Promise<void> {
		log = [];
		progress = UpdateProgress.Updating;
		try {
			await firmwareUpdate(node.id, url, (p: number, l: string) => {
				const currentNonNumericLog = extractNonNumeric(l);
				if (p == -1) progress = UpdateProgress.Failed;
				else percentComplete = p;
				if (lastNonNumericLog === currentNonNumericLog) {
					log[log.length - 1] = l;
					log = log;
				} else {
					log = [...log, l];
				}
				lastNonNumericLog = currentNonNumericLog;
			});
		} catch (e) {
			if (e instanceof Error) {
				console.log(e);
				toastStore.create({ description: e.message, type: 'error' });
			}
		} finally {
			if (progress == UpdateProgress.Updating) progress = UpdateProgress.Success;
		}
	}

	$: selectedFlavor = $firmwareTypes?.flavors?.find((d) => d.value === flavor);
	$: possibleFirmware = $firmwareTypes?.firmware?.filter((d) => d.cpu === cpu && d.flavor == flavor);

	// Base Classes
	const cBase = 'card p-4 w-modal shadow-xl space-y-4';
	const cHeader = 'text-2xl font-bold';
	const cForm = 'border border-surface-500 p-4 space-y-4 rounded-container';
</script>

{#if true}
	{#if progress > UpdateProgress.Form}
		<div class={cBase}>
			<header class={cHeader}>Firmware Update: {percentComplete ?? 0}%...</header>
			{#each log as item}
				<p>{item}</p>
			{/each}
			<Progress value={percentComplete} max={100} />
			{#if progress > UpdateProgress.Updating}
				<footer class="modal-footer {parent.regionFooter}">
					{#if progress == UpdateProgress.Success}
						<button class="btn {parent.buttonPositive}" on:click={parent.onClose}>Close</button>
					{:else}
						<button class="btn {parent.buttonNeutral}" on:click={parent.onClose}>{parent.buttonTextCancel}</button>
						<button class="btn {parent.buttonPositive}" on:click={onFormSubmit}>Retry</button>
					{/if}
				</footer>
			{/if}
		</div>
	{:else if !$firmwareTypes}
		<div class={cBase}>
			<p>Loading...</p>
		</div>
	{:else}
		<div class={cBase}>
			<header class={cHeader}>Firmware Update</header>
			<article>Update firmware for {node.name}</article>
			<!-- Enable for debugging: -->
			<form class="modal-form {cForm}">
				<label class="label">
					<span>Flavor</span>
					<select id="flavor" class="flex-grow select" bind:value={flavor}>
						{#each $firmwareTypes.flavors as item}
							<option value={item.value}>{item.name}</option>
						{/each}
					</select>
				</label>
				<label class="label">
					<span>CPU</span>
					<select id="cpu" class="flex-grow select" bind:value={cpu}>
						{#each selectedFlavor?.cpus ?? [] as item}
							<option value={item}>{$cpuNames?.get(item)}</option>
						{/each}
					</select>
				</label>
				<label class="label">
					<span>Firmware</span>
					<select id="cpu" class="flex-grow select" bind:value={firmware}>
						{#each possibleFirmware ?? [] as item}
							<option value={item.name}>{item.name}</option>
						{/each}
					</select>
				</label>
				<label>
					<span>URL</span>
					<input type="text" id="url" class="input" readonly bind:value={url} />
				</label>
			</form>
			<footer class="modal-footer {parent.regionFooter}">
				<button class="btn {parent.buttonNeutral}" on:click={parent.onClose}>{parent.buttonTextCancel}</button>
				<button class="btn {parent.buttonPositive}" on:click={onFormSubmit}>Update</button>
			</footer>
		</div>
	{/if}
{/if}
