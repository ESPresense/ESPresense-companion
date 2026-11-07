<script lang="ts">
	import { gotoCalibration, gotoDevice, gotoMap } from '#lib/urls.js';

	interface Props {
		deviceId?: string | null;
		deviceName?: string;
		currentView?: 'map' | 'calibration';
	}

	let { deviceId, deviceName = 'Unknown Device', currentView = 'map' }: Props = $props();

	// The two device pages link to each other: the map page explains the current fix,
	// the calibration page's Details panel has the solver and per-node fields for debugging.
	function navigateToOtherView() {
		if (!deviceId) return;
		if (currentView === 'calibration') gotoDevice(deviceId);
		else gotoCalibration(deviceId);
	}

	function getDisplayName(name: string) {
		if (!name || name === 'Unknown Device') return 'Unknown Device';
		if (name.length > 40) {
			return `${name.substring(0, 20)}...${name.substring(name.length - 12)}`;
		}
		return name;
	}

	function navigateToBase() {
		if (currentView === 'calibration') {
			gotoCalibration();
		} else {
			gotoMap();
		}
	}

	function getBaseLabel() {
		return currentView === 'calibration' ? 'Calibration' : 'Map';
	}
</script>

<div class="flex items-center space-x-2 text-sm text-surface-600-400 mb-4 px-4 py-2">
	<button class="hover:text-primary-500 transition-colors" onclick={navigateToBase} aria-label={currentView === 'calibration' ? 'Go to calibration' : 'Go to map'}>
		{getBaseLabel()}
	</button>

	<span>→</span>

	<span class="text-surface-900-100 font-medium" title={deviceName}>
		{getDisplayName(deviceName)}
	</span>

	{#if deviceId}
		<button class="ml-auto hover:text-primary-500 transition-colors" onclick={navigateToOtherView}>
			{currentView === 'calibration' ? 'View on map' : 'Calibration & details'}
		</button>
	{/if}
</div>
