<script lang="ts">
	import { devices } from '$lib/stores';
	import type { Device } from '$lib/types';
	import { createEventDispatcher } from 'svelte';
	import SvelteTable from 'svelte-table';

	let dispatcher = createEventDispatcher();
	let selected = '';

	function select(d: Device) {
		selected = d?.id ?? '';
		dispatcher('selected', d);
	}

	let columns = [
		{ title: 'Id', value: (d) => d.id },
		{ title: 'Name', value: (d) => d.name },
		{ title: 'Room / Floor', value: (d) => d.room?.name ?? d.floor?.name ?? 'n/a' },
		{ title: 'X', value: (d) => d.location?.x },
		{ title: 'Y', value: (d) => d.location?.y },
		{ title: 'Z', value: (d) => d.location?.z },
		{ title: 'Fixes', value: (d) => d.fixes ?? 'n/a' },
		{ title: 'Scale', value: (d) => d.scale ?? 'n/a' },
		{ title: 'Confidence', value: (d) => d.confidence ?? 'n/a' },
		{ title: 'LastHit', value: (d) => d.lastHit ?? 'n/a' }
	];

	function onRowClick(e) {
		select(e.detail.row);
	}
</script>

<div class="table-container">
	{#if $devices}
		<SvelteTable {columns} classNameTable="table table-hover" rows={$devices} on:clickRow={onRowClick} />
	{/if}
</div>

<style>
	.table-container {
		padding: 10px;
	}
</style>
