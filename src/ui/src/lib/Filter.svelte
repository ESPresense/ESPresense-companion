<script lang="ts">
	import { showAllFloors } from '$lib/stores';
	import { SlideToggle } from '@skeletonlabs/skeleton';
</script>

<SlideToggle active="variant-filled-primary" hover="hover:variant-soft-primary" name="slider-label" bind:checked={$showAllFloors}>Show All Floors</SlideToggle>
