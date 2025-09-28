<script lang="ts">
	import { gotoCalibration } from '$lib/urls';

	export let deviceName: string = '';
	export let showDeviceLevel: boolean = true;

</script>

<div class="flex items-center space-x-2 text-sm text-surface-600-400 mb-4">
	<button 
		class="hover:text-primary-500 transition-colors" 
		onclick={gotoCalibration}
		aria-label="Go to main calibration page"
	>
		Calibration
	</button>
	
	{#if showDeviceLevel}
		<span>→</span>
		<button 
			class="hover:text-primary-500 transition-colors" 
			onclick={gotoCalibration}
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
