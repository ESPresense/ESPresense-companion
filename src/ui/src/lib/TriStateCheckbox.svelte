<script lang="ts">
	import { createEventDispatcher } from 'svelte';

	export let checked: boolean | null = false;
	export let id: string;
	const dispatch = createEventDispatcher();

	function handleClick(event: Event) {
		const cb = event.target as HTMLInputElement;
		if (cb.readOnly) {
			cb.checked = false;
			cb.readOnly = false;
			checked = false;
		} else if (!cb.checked) {
			cb.readOnly = true;
			cb.indeterminate = true;
			checked = null;
		} else {
			checked = true;
		}
		dispatch('change', { checked });
	}

	$: ariaChecked = checked === null ? 'mixed' : checked;
</script>

<input type="checkbox" class="checkbox" {id} on:click={handleClick} checked={checked === true} indeterminate={checked === null} readOnly={checked === null} aria-checked={ariaChecked} />

<style>
	input[type='checkbox']:indeterminate {
		background-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'%3e%3cpath fill='none' stroke='%23fff' stroke-linecap='round' stroke-linejoin='round' stroke-width='3' d='M6 10h8'/%3e%3c/svg%3e");
	}
</style>
