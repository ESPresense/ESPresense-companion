<script lang="ts">
	interface Props {
		name: string;
		checked: boolean;
		disabled?: boolean;
		size?: 'sm' | 'md' | 'lg';
		children?: any;
	}

	let { name, checked = $bindable(), disabled = false, size = 'md', children }: Props = $props();

	function toggle() {
		if (!disabled) {
			checked = !checked;
		}
	}

	// Size variants (without translate, which needs to be reactive)
	const sizes = {
		sm: {
			track: 'h-5 w-9',
			thumb: 'h-3 w-3',
			translateChecked: 'translate-x-5',
			translateUnchecked: 'translate-x-1'
		},
		md: {
			track: 'h-6 w-11',
			thumb: 'h-4 w-4',
			translateChecked: 'translate-x-6',
			translateUnchecked: 'translate-x-1'
		},
		lg: {
			track: 'h-7 w-13',
			thumb: 'h-5 w-5',
			translateChecked: 'translate-x-7',
			translateUnchecked: 'translate-x-1'
		}
	};

	let currentSize = $derived(sizes[size]);
	let translate = $derived(checked ? currentSize.translateChecked : currentSize.translateUnchecked);
	let trackClasses = $derived(`relative inline-flex items-center rounded-full transition-colors border-2 ${currentSize.track} ${checked ? 'bg-primary-500 border-primary-600' : 'bg-surface-300 border-surface-400 hover:border-surface-500'} ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer hover:shadow-sm'}`);
	let thumbClasses = $derived(`inline-block transform rounded-full bg-white shadow-sm transition-transform ${currentSize.thumb} ${translate}`);
</script>

<div class="flex items-center space-x-3">
	<button class={trackClasses} onclick={toggle} {disabled} aria-label="Toggle {name}" role="switch" aria-checked={checked} aria-describedby="{name}-label">
		<span class={thumbClasses}></span>
	</button>
	{#if children}
		<span id="{name}-label" class="text-sm font-medium text-surface-700-200-token">
			{@render children()}
		</span>
	{/if}
</div>
