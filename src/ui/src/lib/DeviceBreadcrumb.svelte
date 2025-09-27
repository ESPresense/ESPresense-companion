<script lang="ts">
	import { gotoCalibration, gotoMap } from '$lib/urls';

	export let deviceName: string = 'Unknown Device';
	export let currentView: 'map' | 'calibration' = 'map';

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
	<button
		class="hover:text-primary-500 transition-colors"
		onclick={navigateToBase}
		aria-label={currentView === 'calibration' ? 'Go to calibration' : 'Go to map'}
	>
		{getBaseLabel()}
	</button>

	<span>→</span>

	<span class="text-surface-900-100 font-medium" title={deviceName}>
		{getDisplayName(deviceName)}
	</span>
</div>
