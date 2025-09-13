<script lang="ts">
	import { base } from '$app/paths';
	import { goto } from '$app/navigation';

	export let deviceName: string = 'Unknown Device';
	export let currentView: 'map' | 'calibration' = 'map';

	// Helper function to shorten very long device IDs
	function getDisplayName(name: string) {
		if (!name || name === 'Unknown Device') return 'Unknown Device';
		
		// If it's a very long ID (like iBeacon), show first part + "..." + last part
		if (name.length > 40) {
			return `${name.substring(0, 20)}...${name.substring(name.length - 12)}`;
		}
		return name;
	}

	function navigateToDevices() {
		goto(`${base}/devices`);
	}

	function getCurrentViewLabel() {
		return currentView === 'calibration' ? 'Calibration' : 'Map';
	}
</script>

<div class="flex items-center space-x-2 text-sm text-surface-600-400 mb-4 px-4 py-2">
	<button 
		class="hover:text-primary-500 transition-colors" 
		on:click={navigateToDevices}
		aria-label="Go to devices list"
	>
		Devices
	</button>
	
	<span>→</span>
	
	<span class="text-surface-900-100 font-medium" title={deviceName}>
		{getDisplayName(deviceName)}
	</span>
	
	<span>→</span>
	
	<span class="text-surface-900-100 font-medium">
		{getCurrentViewLabel()}
	</span>
</div>