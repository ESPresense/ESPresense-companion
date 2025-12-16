<script lang="ts">
	import { getContext } from 'svelte';
	import { spring, tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';
	import { interpolateLab } from 'd3-interpolate';
	import { fade } from 'svelte/transition';

	import type { Device, LayerCakeContext } from '$lib/types';
	import { config } from '$lib/stores';
	import { getRoomColor } from '$lib/colors';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');
	export let d: Device;
	export let visible: boolean;
	export let onhovered: ((device: Device | null) => void) | undefined = undefined;
	export let onselected: ((device: Device) => void) | undefined = undefined;

	const r = spring(5, { stiffness: 0.15, damping: 0.3 });
	const s = tweened(1, { duration: 500, easing: cubicOut });
	const x = spring(d?.location?.x);
	const y = spring(d?.location?.y);
	const c = tweened(undefined, { duration: 1000, easing: cubicOut, interpolate: interpolateLab });

	$: x.set(d?.location?.x);
	$: y.set(d?.location?.y);
	$: c.set(visible && d?.room?.id ? getRoomColor($config, d?.room?.id) : '#000');

	let hovered = '';

	function hover(d: Device | null) {
		r.set(d == null ? 5 : 10);
		s.set(d == null ? 1 : 2);
		hovered = d?.id ?? '';
		onhovered?.(d);
	}

	function unselect() {}

	function select(d: Device) {
		onselected?.(d);
	}
</script>

{#if visible && d.confidence > 1 && d.location}
	<g in:fade={{ duration: 1000 }} out:fade={{ duration: 1000 }}>
		{#if d.isAnchored ?? false}
			<path role="none" d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7z" transform="translate({$xScale($x) - 12}, {$yScale($y) - 24})" fill={$c} stroke={d.id == hovered ? 'black' : 'white'} stroke-width={$s} onmouseover={() => hover(d)} onfocus={() => select(d)} onmouseout={() => hover(null)} onblur={() => unselect()} />
			<circle cx={$xScale($x)} cy={$yScale($y) - 15} r={3} fill="white" pointer-events="none" />
		{:else}
			<circle role="none" cx={$xScale($x)} cy={$yScale($y)} fill={$c} stroke={d.id == hovered ? 'black' : 'white'} stroke-width={$s} r={$r} onmouseover={() => hover(d)} onfocus={() => select(d)} onmouseout={() => hover(null)} onblur={() => unselect()} />
		{/if}
		<text x={$xScale($x) + 7} y={$yScale($y) + 3} fill="white" font-size="10px">{d.name ?? d.id}</text>
	</g>
{/if}
