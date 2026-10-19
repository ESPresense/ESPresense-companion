<script lang="ts">
	import { onDestroy } from 'svelte';
	import { apiFetch } from '$lib/api';
	import type { DeviceSetting, DeviceSettingsDetails } from '$lib/types';

	let {
		deviceId,
		floorId,
		position,
		currentRefRssi = null,
		oncalibrated
	}: {
		deviceId: string | null;
		floorId: string | null;
		position: { x: number; y: number; z: number } | null;
		currentRefRssi?: number | null;
		oncalibrated?: (settings: DeviceSetting) => void;
	} = $props();
	let collecting = $state(false);
	let message = $state('');
	let failed = $state(false);
	let proposal = $state<{ id: string; value: number } | null>(null);
	let applying = $state(false);
	let calibrationMessage = $state('');
	let lastSample = $state<{ deviceId: string; sampleId: string } | null>(null);
	let undoing = $state(false);
	let request: AbortController | undefined;

	onDestroy(() => request?.abort());

	async function collect() {
		if (!deviceId || !position || !floorId || collecting || applying || undoing) return;
		const id = deviceId;
		const spot = { ...position, floor: floorId };
		proposal = null;
		calibrationMessage = '';
		request = new AbortController();
		collecting = true;
		failed = false;
		message = `Stay still at (${spot.x.toFixed(1)}, ${spot.y.toFixed(1)}) for 15 seconds…`;
		try {
			const result = await apiFetch<{ sampleId: string; nodes: number; enabled: boolean; calibration: number | null }>(`/api/device/${encodeURIComponent(id)}/fingerprint`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(spot),
				signal: request.signal
			});
			if (id !== deviceId) return;
			lastSample = { deviceId: id, sampleId: result.sampleId };
			if (result.calibration != null) proposal = { id, value: result.calibration };
			else calibrationMessage = 'No calibration suggestion: more readings from nodes with known positions are needed.';
			message = `Fingerprint saved from ${result.nodes} nodes. ${result.enabled ? 'It is ready for positioning.' : 'Enable the fingerprint locator in configuration to use saved samples.'}`;
		} catch (error) {
			failed = true;
			message = error instanceof Error && error.name === 'AbortError' ? 'Collection cancelled.' : error instanceof Error ? error.message : 'Could not save fingerprint.';
		} finally {
			collecting = false;
			request = undefined;
		}
	}
	async function undoSample() {
		if (!lastSample || lastSample.deviceId !== deviceId || collecting || applying || undoing) return;
		const sample = lastSample;
		undoing = true;
		failed = false;
		try {
			await apiFetch(`/api/device/${encodeURIComponent(sample.deviceId)}/fingerprint/${encodeURIComponent(sample.sampleId)}`, { method: 'DELETE' });
			lastSample = null;
			proposal = null;
			message = 'Last fingerprint sample removed.';
			calibrationMessage = 'Device calibration is unchanged, including any update you already applied.';
		} catch (error) {
			failed = true;
			message = error instanceof Error ? error.message : 'Could not remove the sample. Try again.';
		} finally {
			undoing = false;
		}
	}

	async function applyCalibration() {
		if (!proposal || proposal.id !== deviceId || applying || collecting || undoing) return;
		const { id, value } = proposal;
		applying = true;
		try {
			// Fetch current settings so applying a suggestion does not overwrite a newer name or anchor.
			const latest = await apiFetch<DeviceSettingsDetails>(`/api/device/${encodeURIComponent(id)}`);
			if (!latest.settings) throw new Error('Device settings are unavailable.');
			const updated = { ...latest.settings, 'rssi@1m': value };
			await apiFetch(`/api/device/${encodeURIComponent(latest.settings.originalId || id)}`, {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(updated)
			});
			oncalibrated?.(updated);
			proposal = null;
			calibrationMessage = 'Calibration updated. Your saved fingerprint is unchanged.';
		} catch (error) {
			calibrationMessage = error instanceof Error ? error.message : 'Could not update calibration. Your fingerprint is saved.';
		} finally {
			applying = false;
		}
	}
</script>

<div class="card p-4 preset-tonal">
	<header class="font-semibold mb-2">Teach this location</header>
	<p class="text-sm mb-3">Place the map marker at this device's actual location and check the floor and height. Confirm below, then keep the device still for 15 seconds to save a fingerprint and calculate an optional calibration update. Your calibration changes only when you apply the suggestion.</p>
	<button class="btn preset-filled-primary-500" onclick={collect} disabled={collecting || applying || undoing || !deviceId || !position || !floorId}>
		{collecting ? 'Collecting…' : 'Collect sample'}
	</button>
	{#if collecting}
		<button class="btn preset-filled-surface-500" onclick={() => request?.abort()}>Cancel</button>
	{/if}
	{#if lastSample && lastSample.deviceId === deviceId}
		<button class="btn preset-filled-surface-500" onclick={undoSample} disabled={collecting || applying || undoing}>{undoing ? 'Removing…' : 'Undo last sample'}</button>
	{/if}
	<p class="text-sm mt-2" class:text-error-500={failed} role="status">{message}</p>
	{#if proposal && proposal.id === deviceId}
		<div class="mt-3 space-y-2">
			<p class="text-sm">Device calibration (RSSI at 1 m): {currentRefRssi ?? 'not set'} → {proposal.value} dBm.</p>
			<p class="text-sm">Applying this value updates distance estimates for this device.</p>
			<button class="btn preset-filled-secondary-500" onclick={applyCalibration} disabled={applying || collecting || undoing}>{applying ? 'Applying…' : 'Apply suggested calibration'}</button>
			<button
				class="btn preset-filled-surface-500"
				onclick={() => {
					proposal = null;
					calibrationMessage = 'Existing calibration kept. Your fingerprint is saved.';
				}}
				disabled={applying || undoing}>Keep current calibration</button
			>
		</div>
	{/if}
	<p class="text-sm mt-2" aria-live="polite">{calibrationMessage}</p>
</div>
