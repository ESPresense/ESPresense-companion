<script lang="ts">
	import { spring, tweened } from 'svelte/motion';
	import { cubicOut } from 'svelte/easing';
	import { interpolateLab } from 'd3-interpolate';
	import { fade } from 'svelte/transition';

	import { getLayerCakeContext } from 'layercake';
	import type { Device, MapScales } from '#lib/types.js';
	import { config } from '#lib/stores.js';
	import { getRoomColor } from '#lib/colors.js';

	const chart = getLayerCakeContext<MapScales>();
	interface Props {
		d: Device;
		visible: boolean;
		onhovered?: ((device: Device | null) => void) | undefined;
		onselected?: ((device: Device) => void) | undefined;
	}

	let { d, visible, onhovered = undefined, onselected = undefined }: Props = $props();

	const r = spring(5, { stiffness: 0.15, damping: 0.3 });
	const s = tweened(1, { duration: 500, easing: cubicOut });
	// Springs must never be set to undefined: that yields NaN screen coordinates (SVG "Expected length, NaN"),
	// including while the marker fades out after losing its location. Keep the last known position instead.
	const x = spring(d?.location?.x ?? 0);
	const y = spring(d?.location?.y ?? 0);
	let placed = d?.location?.x != null && d?.location?.y != null;
	// tweened() sets an initial undefined value without interpolating, so the fallback never runs
	const c = tweened<string | undefined>(undefined, { duration: 1000, easing: cubicOut, interpolate: (a, b) => interpolateLab(a ?? '#000', b ?? '#000') });

	$effect(() => {
		if (d?.location?.x != null) x.set(d.location.x, { hard: !placed });
	});
	$effect(() => {
		if (d?.location?.y != null) y.set(d.location.y, { hard: !placed });
		if (d?.location?.x != null && d?.location?.y != null) placed = true;
	});
	$effect(() => {
		c.set(visible && d?.room?.id ? getRoomColor($config, d?.room?.id) : '#000');
	});

	let hovered = $state('');

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
			<path role="none" d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7z" transform="translate({chart.xScale($x) - 12}, {chart.yScale($y) - 24})" fill={$c} stroke={d.id == hovered ? 'black' : 'white'} stroke-width={$s} onmouseover={() => hover(d)} onfocus={() => select(d)} onmouseout={() => hover(null)} onblur={() => unselect()} />
			<circle cx={chart.xScale($x)} cy={chart.yScale($y) - 15} r={3} fill="white" pointer-events="none" />
		{:else}
			<circle role="none" cx={chart.xScale($x)} cy={chart.yScale($y)} fill={$c} stroke={d.id == hovered ? 'black' : 'white'} stroke-width={$s} r={$r} onmouseover={() => hover(d)} onfocus={() => select(d)} onmouseout={() => hover(null)} onblur={() => unselect()} />
		{/if}
		<text x={chart.xScale($x) + 7} y={chart.yScale($y) + 3} fill="white" font-size="10px">{d.name ?? d.id}</text>
	</g>
{/if}
