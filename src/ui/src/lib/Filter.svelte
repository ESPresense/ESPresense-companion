<script lang="ts">
	import { showAllFloors } from '$lib/stores';
	import { Switch } from '@skeletonlabs/skeleton-svelte';
</script>

<Switch active="preset-filled-primary-500" hover="hover:preset-tonal-primary" name="slider-label" bind:checked={$showAllFloors}>Show All Floors</Switch>
