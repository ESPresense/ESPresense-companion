<script lang="ts">
	import * as urls from '$lib/urls';

	export let deviceName: string = 'Unknown Device';

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
		urls.gotoDevices();
	}

</script>

<div class="flex items-center space-x-2 text-sm text-surface-600-400 mb-4 px-4 py-2">
	<button 
		class="hover:text-primary-500 transition-colors" 
		onclick={navigateToDevices}
		aria-label="Go to devices list"
	>
		Devices
	</button>
	
	<span>→</span>
	
	<span class="text-surface-900-100 font-medium" title={deviceName}>
		{getDisplayName(deviceName)}
	</span>
</div>
