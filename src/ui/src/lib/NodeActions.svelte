<script lang="ts">
	import { base } from '$app/paths';
	import { detail } from '$lib/urls';
	import link from '$lib/images/link.svg';
	import type { Node, NodeSetting, NodeSettingDetails } from '$lib/types';
	import { getModalStore, getToastStore, type ToastSettings } from '@skeletonlabs/skeleton';
	import { updateMethod, firmwareSource, flavor, version, artifact, flavorNames, firmwareTypes, getLocalFirmwareUrl, getFirmwareUrl } from '$lib/firmware';
	import Firmware from '$lib/modals/Firmware.svelte';
	import NodeSettingsModal from './NodeSettingsModal.svelte';

	const modalStore = getModalStore();
	const toastStore = getToastStore();
	let loadingEdit = false; // Loading state for edit button

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
		// Ensure selectedFlavorId is defined before using it as a map key
		const flavorName = selectedFlavorId ? $flavorNames?.get(selectedFlavorId) : undefined;

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

	// Function to handle map navigation using detail
	function handleMap() {
		detail(row);
		// Note: The original navigation went to /map?nodeId=...
		// The detail function navigates to /nodes/{id}. This aligns with the user's provided detail function logic.
	}

	// Added handleEdit function, similar to DeviceActions
	async function handleEdit() {
		if (!row) return;
		loadingEdit = true;
		try {
			const response = await fetch(`${base}/api/node/${row.id}`);
			if (!response.ok) {
				throw new Error(`Failed to fetch node settings details: ${response.statusText}`);
			}
			const nodeSettingsDetails: NodeSettingDetails = await response.json();

			// Check if the settings object exists within the details
			if (!nodeSettingsDetails.settings) {
				// Attempt to create a default settings object if null
				const defaultSettings: NodeSetting = {
					id: row.id, // Use the row ID as the default ID
					name: row.name || null, // Use row name or null
					updating: { autoUpdate: null, prerelease: null },
					scanning: { forgetAfterMs: null },
					counting: { idPrefixes: null, minDistance: null, maxDistance: null, minMs: null },
					filtering: { includeIds: null, excludeIds: null, maxDistance: null, skipDistance: null, skipMs: null },
					calibration: { rxRefRssi: null, rxAdjRssi: null, absorption: null, txRefRssi: null }
				};
				nodeSettingsDetails.settings = defaultSettings;
				// Log a warning or info that default settings are being used
				console.warn(`Node settings for ${row.id} not found in API response, using defaults.`);
				// Optionally, you could throw an error here if default settings are not acceptable:
				// throw new Error('Node settings not found in API response.');
			}

			const nodeSetting: NodeSetting = nodeSettingsDetails.settings; // Extract the settings

			modalStore.trigger({
				type: 'component',
				// Pass the extracted nodeSetting to the modal
				component: { ref: NodeSettingsModal, props: { nodeSetting: nodeSetting } },
				title: `Edit Settings for ${nodeSetting.name || nodeSetting.id}` // Use extracted settings for title
			});
		} catch (ex) {
			console.error('Error fetching node settings for modal:', ex);
			let errorMessage = 'An unknown error occurred while loading node settings.';
			if (ex instanceof Error) {
				errorMessage = `Error loading node settings: ${ex.message}`;
			}
			const t: ToastSettings = { message: errorMessage, background: 'variant-filled-error' };
			toastStore.trigger(t);
		} finally {
			loadingEdit = false;
		}
	}

	export let row: Node;
	export let col: string;
	$: _ = col;
</script>

{#if row.online}
	<!-- Edit button (Primary) -->
	<button class="btn btn-sm variant-filled-primary" on:click|stopPropagation={handleEdit} disabled={loadingEdit}>
		{#if loadingEdit}
			<span class="loading loading-spinner loading-xs"></span>
		{:else}
			Edit
		{/if}
	</button>

	<!-- Map button (Secondary) -->
	<button class="btn btn-sm variant-filled-secondary" on:click|stopPropagation={handleMap}>Map</button>

	{#if row.telemetry?.version}
		<button on:click={() => onUpdate(row)} disabled={!($updateMethod == 'self' || ($firmwareSource == 'release' && $version) || ($firmwareSource == 'artifact' && $artifact))} class="btn btn-sm variant-filled-tertiary">Update</button>
	{/if}

	{#if row.telemetry}
		<button on:click={() => onRestart(row)} class="btn btn-sm variant-filled-warning">Restart</button>
	{/if}

	{#if row.telemetry?.ip}
		<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm variant-filled">
			<span>Visit</span>
			<span><img class="w-4" src={link} alt="External Link" /></span>
		</a>
	{/if}
{/if}
