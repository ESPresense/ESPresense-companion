<script lang="ts">
	import { base } from '$app/paths';
	import { goto } from '$app/navigation';

	export let deviceName: string = '';
	export let showDeviceLevel: boolean = true;

	function navigateToCalibration() {
		goto(`${base}/calibration`);
	}

	function navigateToDeviceCalibration() {
		goto(`${base}/calibration`);
		// Note: We could add URL params to auto-select device tab
		// But for now, users can click the tab manually
	}
</script>

<div class="flex items-center space-x-2 text-sm text-surface-600-400 mb-4">
	<button 
		class="hover:text-primary-500 transition-colors" 
		on:click={navigateToCalibration}
		aria-label="Go to main calibration page"
	>
		Calibration
	</button>
	
	{#if showDeviceLevel}
		<span>→</span>
		<button 
			class="hover:text-primary-500 transition-colors" 
			on:click={navigateToDeviceCalibration}
			aria-label="Go to device calibration list"
		>
			Device Calibration
		</button>
		
		{#if deviceName}
			<span>→</span>
			<span class="text-surface-900-100 font-medium">{deviceName}</span>
		{/if}
	{/if}
</div>