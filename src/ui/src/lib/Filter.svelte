<script lang="ts">
	import SlideToggle from './SlideToggle.svelte';
	import { showAllFloors } from '$lib/stores';
</script>

<SlideToggle name="show-all-floors" bind:checked={$showAllFloors}>
	<span class="text-white">Show All Floors</span>
</SlideToggle>
