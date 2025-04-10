<script lang="ts">
	import { base } from '$app/paths';
	import { detail } from '$lib/urls';
	import link from '$lib/images/link.svg';
	import type { Node, NodeSetting, NodeSettingDetails } from '$lib/types';
	import { Popover } from '@skeletonlabs/skeleton-svelte';
	import { updateMethod, firmwareSource, flavor, version, artifact, flavorNames, firmwareTypes, getLocalFirmwareUrl, getFirmwareUrl } from '$lib/firmware';
	import Firmware from '$lib/modals/Firmware.svelte';
	import NodeSettingsModal from './NodeSettingsModal.svelte';
	import { toaster as toastStore } from '$lib/toaster';

	export let row: Node; // Node data for this row
	export let col: string; // Column identifier from parent table
	$: _ = col; // Suppress unused variable warning while preserving the prop

	let loadingEdit = false;
	let showSettingsPopover = false;
	let nodeSetting: NodeSetting | null = null;
	let showFirmwarePopover = false;
	let firmwareProps: any = null;

	async function onRestart(node: Node) {
		try {
			const response = await fetch(`${base}/api/node/${node.id}/restart`, { method: 'POST' });
			if (!response.ok) throw new Error(response.statusText || 'Failed to restart node');

			toastStore.success({
				title: 'Reboot Requested',
				description: `${node.name || node.id} asked to reboot`
			});
		} catch (error) {
			console.error(error);
			toastStore.error({
				title: 'Error',
				description: error instanceof Error ? error.message : 'Failed to restart node'
			});
		}
	}

	function getUpdateDescription(flavorId: string | undefined): string {
		const selectedFlavorId = $flavor === '-' ? flavorId : $flavor;
		const flavorName = selectedFlavorId ? $flavorNames?.get(selectedFlavorId) : undefined;

		let description = '';

		if ($updateMethod === 'self') {
			description = 'with latest firmware';
		} else if (['manual', 'recovery'].includes($updateMethod)) {
			if ($firmwareSource === 'release') {
				description = `with github version ${$version}`;
			} else if ($firmwareSource === 'artifact') {
				description = `with github artifact ${$artifact}`;
			}
		}

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
			if ($updateMethod !== 'self') {
				const matchingFirmware = $firmwareTypes?.firmware?.filter((d) => d.cpu === cpuId && d.flavor === selectedFlavorId);
				const firmwareId = matchingFirmware?.length === 1 ? matchingFirmware[0].name : null;

				if (!firmwareId) {
					throw new Error(`No firmware found for selected CPU (${cpuId}) and flavor (${selectedFlavorId})`);
				}

				url = $artifact ? getLocalFirmwareUrl($firmwareSource, $version, $artifact, firmwareId) : getFirmwareUrl($firmwareSource, $version, $artifact, firmwareId);

				if (!url) {
					throw new Error(`No firmware URL found for ${$firmwareSource}, ${$version || $artifact}, ${cpuId}, ${selectedFlavorId}`);
				}
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

			toastStore.success({
				title: 'Update Requested',
				description: `${node.name || node.id} asked to update ${updateDescription}`
			});
		} catch (error) {
			console.error(error);
			toastStore.error({
				title: 'Error',
				description: error instanceof Error ? error.message : 'Update failed'
			});
		}
	}

	function onUpdate(node: Node) {
		const flavorValue = $flavor === '-' ? node.flavor?.value : $flavor;
		const updateDescription = getUpdateDescription(node.flavor?.value);

		if ($updateMethod === 'recovery') {
			firmwareProps = {
				node,
				firmwareSource: $firmwareSource,
				flavor: flavorValue,
				cpu: node.cpu?.value,
				version: $version,
				artifact: $artifact
			};
			showFirmwarePopover = true;
		} else {
			handleStandardUpdate(node, flavorValue, updateDescription);
		}
	}

	async function handleEdit() {
		if (!row) return;
		loadingEdit = true;

		try {
			const response = await fetch(`${base}/api/node/${row.id}`);
			if (!response.ok) throw new Error(`Failed to fetch node settings details: ${response.statusText}`);

			const nodeSettingsDetails: NodeSettingDetails = await response.json();

			if (!nodeSettingsDetails.settings) {
				// Create default settings if none exist
				const defaultSettings: NodeSetting = {
					id: row.id,
					name: row.name || null,
					updating: { autoUpdate: null, prerelease: null },
					scanning: { forgetAfterMs: null },
					counting: { idPrefixes: null, minDistance: null, maxDistance: null, minMs: null },
					filtering: { includeIds: null, excludeIds: null, maxDistance: null, skipDistance: null, skipMs: null },
					calibration: { rxRefRssi: null, rxAdjRssi: null, absorption: null, txRefRssi: null }
				};
				nodeSettingsDetails.settings = defaultSettings;
				console.warn(`Node settings for ${row.id} not found in API response, using defaults.`);
			}

			nodeSetting = nodeSettingsDetails.settings;

			nodeSetting = nodeSettingsDetails.settings;
			showSettingsPopover = true;
		} catch (ex) {
			console.error('Error fetching node settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading node settings: ${ex.message}` : 'An unknown error occurred while loading node settings.';

			toastStore.error({
				title: 'Error',
				description: errorMessage
			});
		} finally {
			loadingEdit = false;
		}
	}
</script>

<div class="flex gap-1">
	{#if row.online}
		<button class="btn btn-sm preset-filled-primary-500" on:click|stopPropagation={handleEdit} disabled={loadingEdit} aria-label="Edit node settings">
			{#if loadingEdit}
				<span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
			{:else}
				Edit
			{/if}
		</button>

		<button class="btn btn-sm preset-filled-secondary-500" on:click|stopPropagation={() => detail(row)} aria-label="View node on map"> Map </button>

		{#if row.telemetry?.version}
			<button on:click={() => onUpdate(row)} disabled={!($updateMethod === 'self' || ($firmwareSource === 'release' && $version) || ($firmwareSource === 'artifact' && $artifact))} class="btn btn-sm preset-filled-tertiary-500" aria-label="Update node firmware"> Update </button>
		{/if}

		{#if row.telemetry}
			<button on:click={() => onRestart(row)} class="btn btn-sm preset-filled-warning-500" aria-label="Restart node"> Restart </button>
		{/if}

		{#if row.telemetry?.ip}
			<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm preset-filled" aria-label="Open node web interface">
				<span>Visit</span>
				<span><img class="w-4" src={link} alt="External Link" /></span>
			</a>
		{/if}
	{/if}
</div>

{#if showFirmwarePopover && firmwareProps}
	<Popover on:close={() => showFirmwarePopover = false} title={`Update ${firmwareProps.node.name || firmwareProps.node.id} Firmware`}>
		<Firmware {...firmwareProps} />
	</Popover>
{/if}

{#if showSettingsPopover && nodeSetting}
	<Popover on:close={() => showSettingsPopover = false} title={`Edit Settings for ${nodeSetting?.name || nodeSetting?.id || ''}`}>
		<NodeSettingsModal {nodeSetting} on:close={() => showSettingsPopover = false} />
	</Popover>
{/if}
