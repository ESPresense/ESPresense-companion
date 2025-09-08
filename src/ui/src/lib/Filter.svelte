<script lang="ts">
	import { showAllFloors } from '$lib/stores';
	import { SlideToggle } from '$lib/skeleton-v3';
</script>

<SlideToggle active="variant-filled-primary" hover="hover:variant-soft-primary" name="slider-label" bind:checked={$showAllFloors}>Show All Floors</SlideToggle>
