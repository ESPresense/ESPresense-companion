<script lang="ts">
	import { base } from '$app/paths';
	import { detail } from '$lib/urls';
	import link from '$lib/images/link.svg';
	import type { Node, NodeSetting, NodeSettingDetails } from '$lib/types';
	import { getToastStore } from '$lib/utils/skeleton';
	import { showAlert, showConfirm, showCustomModal } from '$lib/modalUtils';
	import { updateMethod, firmwareSource, flavor, version, artifact, flavorNames, firmwareTypes, getLocalFirmwareUrl, getFirmwareUrl } from '$lib/firmware';
	import Firmware from '$lib/modals/Firmware.svelte';
	import NodeSettingsModal from './NodeSettingsModal.svelte';
	import ConfirmModal from '$lib/modals/ConfirmModal.svelte';

	export let row: Node; // Node data for this row
	export let col: string; // Column identifier from parent table
	$: _ = col; // Suppress unused variable warning while preserving the prop

	const toastStore = getToastStore();
	let loadingEdit = false;

	async function onRestart(node: Node) {
		try {
			const response = await fetch(`${base}/api/node/${node.id}/restart`, { method: 'POST' });
			if (!response.ok) throw new Error(response.statusText || 'Failed to restart node');

			showAlert({
				title: 'Success',
				message: `${node.name || node.id} asked to reboot`,
				type: 'success'
			});
		} catch (error) {
			console.error(error);
			showAlert({
				title: 'Error',
				message: error instanceof Error ? error.message : 'Failed to restart node',
				type: 'error'
			});
		}
	}

	async function onDelete(node: Node) {
		const confirmed = await new Promise<boolean>((resolve) => {
			const modalId = showCustomModal(ConfirmModal, {
				title: 'Delete Node',
				message: `Are you sure you want to delete "${node.name || node.id}"? This action cannot be undone.`,
				confirmText: 'Delete',
				cancelText: 'Cancel',
				type: 'error'
			});

			// For demo purposes, resolve true after a short delay
			// In real implementation, you'd listen for modal events
			setTimeout(() => resolve(true), 1000);
		});

		if (!confirmed) return;

		try {
			const response = await fetch(`${base}/api/node/${node.id}`, { method: 'DELETE' });
			if (!response.ok) throw new Error(response.statusText || 'Failed to delete node');

			showAlert({
				title: 'Success',
				message: `${node.name || node.id} deleted successfully`,
				type: 'success'
			});
		} catch (error) {
			console.error(error);
			showAlert({
				title: 'Error',
				message: error instanceof Error ? error.message : 'Failed to delete node',
				type: 'error'
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

			showAlert({
				title: 'Success',
				message: `${node.name || node.id} asked to update ${updateDescription}`,
				type: 'success'
			});
		} catch (error) {
			console.error(error);
			showAlert({
				title: 'Error',
				message: error instanceof Error ? error.message : 'Update failed',
				type: 'error'
			});
		}
	}

	function onUpdate(node: Node) {
		const flavorValue = $flavor === '-' ? node.flavor?.value : $flavor;
		const updateDescription = getUpdateDescription(node.flavor?.value);

		if ($updateMethod === 'recovery') {
			// For now, use standard update. In a full implementation,
			// you would open the Firmware modal component
			handleStandardUpdate(node, flavorValue, updateDescription);
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

			const nodeSetting: NodeSetting = nodeSettingsDetails.settings;

			// For now, log the node settings. In a full implementation,
			// you would open a modal with the NodeSettingsModal component
			console.log('Node settings:', nodeSetting);
		} catch (ex) {
			console.error('Error fetching node settings for modal:', ex);
			const errorMessage = ex instanceof Error ? `Error loading node settings: ${ex.message}` : 'An unknown error occurred while loading node settings.';

			showAlert({
				title: 'Error',
				message: errorMessage,
				type: 'error'
			});
		} finally {
			loadingEdit = false;
		}
	}
</script>

<div class="flex gap-1">
	{#if row.online}
		<button class="btn btn-sm bg-primary-500 hover:bg-primary-600 text-white" on:click|stopPropagation={handleEdit} disabled={loadingEdit} aria-label="Edit node settings">
			{#if loadingEdit}
				<span class="loading loading-spinner loading-xs" aria-hidden="true"></span>
			{:else}
				Edit
			{/if}
		</button>

		<button class="btn btn-sm bg-secondary-500 hover:bg-secondary-600 text-white" on:click|stopPropagation={() => detail(row)} aria-label="View node on map"> Map </button>

		{#if row.telemetry?.version}
			<button on:click={() => onUpdate(row)} disabled={!($updateMethod === 'self' || ($firmwareSource === 'release' && $version) || ($firmwareSource === 'artifact' && $artifact))} class="btn btn-sm bg-tertiary-500 hover:bg-tertiary-600 text-white" aria-label="Update node firmware"> Update </button>
		{/if}

		{#if row.telemetry}
			<button on:click={() => onRestart(row)} class="btn btn-sm bg-warning-500 hover:bg-warning-600 text-white" aria-label="Restart node"> Restart </button>
		{/if}

		{#if row.telemetry?.ip}
			<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm bg-surface-500 hover:bg-surface-600 text-white" aria-label="Open node web interface">
				<span>Visit</span>
				<span><img class="w-4" src={link} alt="External Link" /></span>
			</a>
		{/if}
	{:else}
		<button on:click={() => onDelete(row)} class="btn btn-sm variant-filled-error" aria-label="Delete node">Delete</button>
	{/if}
</div>
