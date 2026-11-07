<script lang="ts">
	// The 3D map's view controls: presets and layer toggles, in the same card style as the 2D map's node links.
	import type { Snippet } from 'svelte';
	import SlideToggle from './SlideToggle.svelte';
	import type { ViewPreset } from './Map3D.svelte';

	interface Props {
		showNodes: boolean;
		showDevices?: boolean;
		/** The device page shows one device and has no use for the toggle. */
		hideDevicesToggle?: boolean;
		autoRotate: boolean;
		onview: (preset: ViewPreset) => void;
		children?: Snippet;
	}

	let { showNodes = $bindable(), showDevices = $bindable(true), hideDevicesToggle = false, autoRotate = $bindable(), onview, children }: Props = $props();
</script>

<div class="fixed bottom-16 right-4 flex flex-col gap-2 rounded bg-surface-100-900/90 p-3 text-sm shadow" style="z-index: 2;" data-testid="map3d-controls">
	<div class="flex items-center justify-between gap-3">
		<span>View</span>
		<div class="flex gap-1">
			<button type="button" class="btn btn-sm preset-tonal" onclick={() => onview('iso')}>Iso</button>
			<button type="button" class="btn btn-sm preset-tonal" onclick={() => onview('top')}>Top</button>
		</div>
	</div>
	{#if !hideDevicesToggle}
		<SlideToggle name="show-devices" bind:checked={showDevices} size="sm">
			<span>Devices</span>
		</SlideToggle>
	{/if}
	<SlideToggle name="show-nodes" bind:checked={showNodes} size="sm">
		<span>Nodes</span>
	</SlideToggle>
	<SlideToggle name="auto-rotate" bind:checked={autoRotate} size="sm">
		<span>Rotate</span>
	</SlideToggle>
	{@render children?.()}
</div>
