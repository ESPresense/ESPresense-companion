<script lang="ts">
	import { base } from '$app/paths';
	import link from '$lib/images/link.svg';
	import type { Node } from '$lib/types';
	import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';
	import { updateMethod, firmwareSource, flavor, version, artifact, flavorNames, firmwareTypes, getLocalFirmwareUrl, getFirmwareUrl } from '$lib/firmware';
	import Firmware from '$lib/modals/Firmware.svelte';

	const modalStore = getModalStore();
	const toastStore = getToastStore();

	async function onRestart(node: Node) {
		try {
			const response = await fetch(`${base}/api/node/${node.id}/restart`, { method: 'POST' });
			if (!response.ok) throw new Error(response.statusText || 'Failed to restart node');

			toastStore.trigger({
				message: `${node.name || node.id} asked to reboot`,
				background: 'variant-filled-primary'
			});
		} catch (error) {
			console.error(error);
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to restart node',
				background: 'variant-filled-error'
			});
		}
	}

	function getUpdateDescription(flavorId: string | undefined): string {
		const selectedFlavorId = $flavor === '-' ? flavorId : $flavor;
		const flavorName = $flavorNames?.get(selectedFlavorId);

		let description = '';

		// Determine base description based on update method and source
		if ($updateMethod === 'self') {
			description = 'with latest firmware';
		} else if (['manual', 'recovery'].includes($updateMethod)) {
			if ($firmwareSource === 'release') {
				description = `with github version ${$version}`;
			} else if ($firmwareSource === 'artifact') {
				description = `with github artifact ${$artifact}`;
			}
		}

		// Add flavor name if available and not default
		if (flavorName && $flavor !== '-') {
			description = `${description} ${flavorName}`;
		}

		return description;
	}

	async function handleStandardUpdate(node: Node, flavorId: string, updateDescription: string) {
		if (!node) return;

		const selectedFlavorId = $flavor === '-' ? flavorId : $flavor;
		const cpuId = node.cpu?.value;
		let url: string | null = null;

		try {
			if ($updateMethod != 'self') {
				const matchingFirmware = $firmwareTypes?.firmware?.filter((d) => d.cpu === cpuId && d.flavor === selectedFlavorId);
				const firmwareId = matchingFirmware?.length === 1 ? matchingFirmware[0].name : null;
				if (!firmwareId) throw new Error(`No firmware found for selected CPU (${cpuId}) and flavor (${selectedFlavorId})`);
				url = $artifact
					? getLocalFirmwareUrl($firmwareSource, $version, $artifact, firmwareId)
					: getFirmwareUrl($firmwareSource, $version, $artifact, firmwareId);
				if (!url) throw new Error(`No firmware URL found for ${$firmwareSource}, ${$version || $artifact}, ${cpuId}, ${selectedFlavorId}`);
			}
			const response = await fetch(`${base}/api/node/${node.id}/update`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({
					cpu: cpuId,
					flavor: selectedFlavorId,
					release: $version,
					artifact: $artifact,
					url: url
				})
			});

			if (!response.ok) throw new Error(response.statusText || 'Update failed');

			const message = `${node.name || node.id} asked to update ${updateDescription}`;
			toastStore.trigger({
				message,
				background: 'variant-filled-primary'
			});
		} catch (error) {
			console.error(error);
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Update failed',
				background: 'variant-filled-error'
			});
		}
	}

	function onUpdate(node: Node) {
		const flavorValue = $flavor === '-' ? node.flavor?.value : $flavor;
		const updateDescription = getUpdateDescription(node.flavor?.value);

		if ($updateMethod === 'recovery') {
			modalStore.trigger({
				title: `Update ${node.name || node.id} Firmware`,
				body: updateDescription,
				type: 'component',
				component: {
					ref: Firmware,
					props: {
						node,
						firmwareSource: $firmwareSource,
						flavor: flavorValue,
						cpu: node.cpu?.value,
						version: $version,
						artifact: $artifact
					}
				}
			});
		} else {
			handleStandardUpdate(node, flavorValue, updateDescription);
		}
	}

	export let row: Node;
	export let col: string;
</script>

{#if row.online}
	{#if row.telemetry?.version}
		<button on:click={() => onUpdate(row)} disabled={!($updateMethod == 'self' || ($firmwareSource == 'release' && $version) || ($firmwareSource == 'artifact' && $artifact))} class="btn btn-sm variant-filled">Update</button>
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
