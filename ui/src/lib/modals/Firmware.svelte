<script lang="ts">
	import { ProgressBar } from '@skeletonlabs/skeleton';
	import { firmwareTypes, cpuNames, getFirmwareUrl, firmwareUpdate } from '$lib/firmware';
	import type { Node } from '$lib/types';
	import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';

	export let updateMethod: string;
	export let node: Node;
	export let flavor: string;
	export let cpu: string;
	export let version: string;
	export let artifact: string;
	export let parent: any;

	enum State {
		Form,
		Updating,
		Failed,
		Success
	}

	const modalStore = getModalStore();
	const toastStore = getToastStore();

	function extractNonNumeric(str: string): string {
		return str.replace(/\d+/g, '');
	}

	let state: State = State.Form;
	let percentComplete: number = 0;
	let firmware: string;
	let url: string;
	let log: string[] = [];
	let lastNonNumericLog: string | null = null;
	$: url = getFirmwareUrl(updateMethod, version, artifact, firmware);

	async function onFormSubmit(): Promise<void> {
		log = [];
		state = State.Updating;
		try {
			await firmwareUpdate(node.id, url, (p: number, l: string) => {
				const currentNonNumericLog = extractNonNumeric(l);
				if (p == -1) state = State.Failed;
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
				const t: ToastSettings = { message: e.message, background: 'variant-filled-error' };
				toastStore.trigger(t);
			}
		} finally {
			if (state == State.Updating) state = State.Success;
		}
	}

	$: selectedFlavor = $firmwareTypes?.flavors?.find((d) => d.value === flavor);
	$: possibleFirmware = $firmwareTypes?.firmware?.filter((d) => d.cpu === cpu && d.flavor == flavor);

	// Base Classes
	const cBase = 'card p-4 w-modal shadow-xl space-y-4';
	const cHeader = 'text-2xl font-bold';
	const cForm = 'border border-surface-500 p-4 space-y-4 rounded-container-token';
</script>

{#if state > State.Form}
	<div class={cBase}>
		<header class={cHeader}>{$modalStore[0].title ?? '(title missing)'}: {percentComplete ?? 0}%...</header>
		{#each log as item}
			<p>{item}</p>
		{/each}
		<ProgressBar bind:value={percentComplete} max={100} />
    {#if state > State.Updating}
    <footer class="modal-footer {parent.regionFooter}">
      {#if state == State.Success}
      <button class="btn {parent.buttonPositive}" on:click={parent.onClose}>Close</button>
      {:else}
      <button class="btn {parent.buttonNeutral}" on:click={parent.onClose}>{parent.buttonTextCancel}</button>
      <button class="btn {parent.buttonPositive}" on:click={onFormSubmit}>Retry</button>
      {/if}
    </footer>
    {/if}
	</div>
{:else}
	{#if !$firmwareTypes }
		<div class={cBase}>
			<p>Loading...</p>
		</div>
	{:else}
		{#if $modalStore[0]}
			<div class={cBase}>
				<header class={cHeader}>{$modalStore[0].title ?? '(title missing)'}</header>
				<article>{$modalStore[0].body ?? '(body missing)'}</article>
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
{/if}
