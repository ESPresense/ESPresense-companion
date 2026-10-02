<script lang="ts">
	interface Props {
		checked?: boolean | null;
		id: string;
		onchange?: ((event: { checked: boolean | null }) => void) | undefined;
		disabled?: boolean;
	}

	let { checked = $bindable(false), id, onchange = undefined, disabled = false }: Props = $props();

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
		onchange?.({ checked });
	}

	let ariaChecked = $derived(checked === null ? ('mixed' as const) : checked);
</script>

<input type="checkbox" class="checkbox" {id} {disabled} onclick={handleClick} checked={checked === true} indeterminate={checked === null} readOnly={checked === null} aria-checked={ariaChecked} />

<style>
	input[type='checkbox']:indeterminate {
		background-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'%3e%3cpath fill='none' stroke='%23fff' stroke-linecap='round' stroke-linejoin='round' stroke-width='3' d='M6 10h8'/%3e%3c/svg%3e");
	}
</style>
