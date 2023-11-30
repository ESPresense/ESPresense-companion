<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import { detail } from '$lib/urls';
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="text-column">
	<h1 class="h1">Devices</h1>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
