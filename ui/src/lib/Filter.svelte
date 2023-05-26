<script lang="ts">
	import { showAll } from '$lib/stores';
	import { SlideToggle } from '@skeletonlabs/skeleton';
</script>

<SlideToggle active="variant-filled-primary" hover="hover:variant-soft-primary" name="slider-label" bind:checked={$showAll}>Show All</SlideToggle>
