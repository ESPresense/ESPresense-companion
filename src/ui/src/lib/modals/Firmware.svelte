<script lang="ts">
        import { Progress as SkeletonProgress } from '@skeletonlabs/skeleton-svelte';
	import { firmwareTypes, cpuNames, getFirmwareUrl, firmwareUpdate } from '$lib/firmware';
	import type { Node } from '$lib/types';
	import { getToastStore } from '$lib/toast/toastStore';

	export let firmwareSource: string;
	export let node: Node;
	export let flavor: string;
	export let cpu: string;
	export let version: string;
	export let artifact: string;
	export let parent: any;

	enum Progress {
		Form,
		Updating,
		Failed,
		Success
	}

	const toastStore = getToastStore();

	function getUpdateDescription(flavorId: string | undefined): string {
		const selectedFlavorId = flavor === '-' ? flavorId : flavor;
		const flavorName = selectedFlavorId ? $firmwareTypes?.flavors?.find((f) => f.value === selectedFlavorId)?.name : undefined;

		let description = '';

		if (firmwareSource === 'release') {
			description = `with github version ${version}`;
		} else if (firmwareSource === 'artifact') {
			description = `with github artifact ${artifact}`;
		}

		if (flavorName && flavor !== '-') {
			description = `${description} ${flavorName}`;
		}

		return description;
	}

	function extractNonNumeric(str: string): string {
		return str.replace(/\d+/g, '');
	}

	let progress: Progress = Progress.Form;
	let percentComplete: number = 0;
	let firmware: string;
	let url: string;
	let log: string[] = [];
	let lastNonNumericLog: string | null = null;
	$: url = getFirmwareUrl(firmwareSource, version, artifact, firmware) ?? '#ERR';

	async function onFormSubmit(): Promise<void> {
		if (!isValidForm) return;

		log = [];
		progress = Progress.Updating;
		try {
			await firmwareUpdate(node.id, url, (p: number, l: string) => {
				const currentNonNumericLog = extractNonNumeric(l);
				if (p == -1) progress = Progress.Failed;
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
				toastStore.trigger({ message: e.message, background: 'preset-filled-error-500' });
			}
		} finally {
			if (progress == Progress.Updating) progress = Progress.Success;
		}
	}

	$: selectedFlavor = $firmwareTypes?.flavors?.find((d) => d.value === flavor);
	$: possibleFirmware = $firmwareTypes?.firmware?.filter((d) => d.cpu === cpu && d.flavor == flavor);
	$: isValidForm = $firmwareTypes && flavor && cpu && firmware && url && url !== '#ERR';

	// Base Classes
	const cBase = 'w-modal space-y-4';
	const cHeader = 'text-2xl font-bold';
	const cForm = 'border border-surface-500 p-4 space-y-4';
</script>

{#if progress > Progress.Form}
	<div class={cBase}>
		<header class={cHeader}>Firmware Update: {percentComplete ?? 0}%...</header>
		{#each log as item}
			<p>{item}</p>
		{/each}
                <SkeletonProgress value={percentComplete} max={100}>
                        <SkeletonProgress.Track class="w-full">
                                <SkeletonProgress.Range />
                        </SkeletonProgress.Track>
                </SkeletonProgress>
		{#if progress > Progress.Updating}
			<footer class="modal-footer {parent.regionFooter}">
				{#if progress == Progress.Success}
					<button class="btn {parent.buttonPositive}" onclick={parent.onClose}>Close</button>
				{:else}
					<button class="btn {parent.buttonNeutral}" onclick={parent.onClose}>{parent.buttonTextCancel}</button>
					<button class="btn {parent.buttonPositive}" onclick={onFormSubmit}>Retry</button>
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
		<header class={cHeader}>Update {node.name} Firmware</header>
		<p class="text-surface-700-300 mb-4">{getUpdateDescription(node.flavor?.value)}</p>
		<article>Select firmware options and click Update to proceed.</article>
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
				<select id="firmware" class="flex-grow select" bind:value={firmware}>
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
			<button class="btn {parent.buttonNeutral}" onclick={parent.onClose}>{parent.buttonTextCancel}</button>
			<button class="btn {parent.buttonPositive}" onclick={onFormSubmit} disabled={!isValidForm}>Update</button>
		</footer>
	</div>
{/if}
