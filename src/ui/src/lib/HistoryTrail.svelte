<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { getLayerCakeContext } from 'layercake';
	import { config } from '#lib/stores.js';
	import { getRoomColor } from '#lib/colors.js';
	import { formatClock, type HistoryView } from '#lib/history.js';
	import type { MapScales } from '#lib/types.js';

	const chart = getLayerCakeContext<MapScales>();

	interface Props {
		transform?: any;
		view: HistoryView;
	}

	let { transform = zoomIdentity, view }: Props = $props();

	// Each segment fades with the age of its newer end, so the path dies away towards the window's start.
	let segments = $derived(view.trail.slice(1).map((p, i) => ({ a: view.trail[i].fix, b: p.fix, opacity: 0.15 + 0.85 * (1 - p.age) })));
	// The stored position at the moment shown. Live, the device's own marker stands there instead.
	let ghost = $derived(view.live ? null : view.current);
	let ghostColor = $derived(getRoomColor($config, view.room?.id));
</script>

<g transform={transform.toString()} pointer-events="none" data-testid="history-trail">
	{#each segments as s}
		<line x1={chart.xScale(s.a.x)} y1={chart.yScale(s.a.y)} x2={chart.xScale(s.b.x)} y2={chart.yScale(s.b.y)} stroke="white" stroke-opacity={s.opacity} stroke-width="1.5" stroke-linecap="round" vector-effect="non-scaling-stroke" />
	{/each}
	{#each view.trail as p}
		<circle cx={chart.xScale(p.fix.x)} cy={chart.yScale(p.fix.y)} r="2" fill={getRoomColor($config, p.room?.id)} fill-opacity={0.25 + 0.75 * (1 - p.age)} data-testid="history-point" />
	{/each}
	{#if ghost}
		<g data-testid="history-ghost">
			<circle cx={chart.xScale(ghost.x)} cy={chart.yScale(ghost.y)} r="6" fill={ghostColor} stroke="white" stroke-width="2" />
			<text x={chart.xScale(ghost.x) + 9} y={chart.yScale(ghost.y) + 3} fill="white" font-size="10px">{formatClock(view.cursor)}</text>
		</g>
	{/if}
</g>
