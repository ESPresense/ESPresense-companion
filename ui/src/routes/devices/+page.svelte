<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import { goto } from '$app/navigation';
	import type { Device } from '$lib/types';

	function detail(d: Device | null) {
		goto(`/devices/${d?.id}`);
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="text-column">
	<h1 class="h1">Devices</h1>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
