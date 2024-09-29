<script lang="ts">
	import DevicesTable from '$lib/DevicesTable.svelte';
	import { detail } from '$lib/urls';
</script>

<svelte:head>
	<title>ESPresense Companion: Devices</title>
</svelte:head>

<div class="container mx-auto p-2">
	<h1 class="text-3xl font-bold my-2 px-2">Devices</h1>
	<DevicesTable on:selected={(d) => detail(d.detail)} />
</div>
