<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { getLayerCakeContext } from 'layercake';
	import { config } from '#lib/stores.js';
	import { getRoomColor } from '#lib/colors.js';
	import { formatClock, type HistoryView, type TrailPoint } from '#lib/history.js';
	import type { MapScales } from '#lib/types.js';

	const chart = getLayerCakeContext<MapScales>();

	interface Props {
		transform?: any;
		view: HistoryView;
		/** The floor the map shows: only fixes on it are drawn, so a walk upstairs doesn't leave its first-floor points on the second-floor map. */
		floorId?: string | null;
	}

	let { transform = zoomIdentity, view, floorId = null }: Props = $props();

	let onThisFloor = $derived((p: TrailPoint) => p.floor?.id === floorId);
	let points = $derived(view.trail.filter(onThisFloor));
	// A segment joins consecutive fixes on this floor and fades with the age of its newer end, so the path dies away
	// towards the window's start. Where the device was on another floor in between, the path breaks.
	let segments = $derived(
		view.trail
			.slice(1)
			.map((p, i) => ({ a: view.trail[i], b: p }))
			.filter((s) => onThisFloor(s.a) && onThisFloor(s.b))
			.map((s) => ({ a: s.a.fix, b: s.b.fix, opacity: 0.15 + 0.85 * (1 - s.b.age) }))
	);
	// The stored position at the moment shown. Live, the device's own marker stands there instead.
	let ghost = $derived(view.live ? null : view.current);
	let ghostColor = $derived(getRoomColor($config, view.room?.id));
</script>

<g transform={transform.toString()} pointer-events="none" data-testid="history-trail">
	{#each segments as s}
		<line x1={chart.xScale(s.a.x)} y1={chart.yScale(s.a.y)} x2={chart.xScale(s.b.x)} y2={chart.yScale(s.b.y)} stroke="white" stroke-opacity={s.opacity} stroke-width="1.5" stroke-linecap="round" vector-effect="non-scaling-stroke" />
	{/each}
	{#each points as p}
		<circle cx={chart.xScale(p.fix.x)} cy={chart.yScale(p.fix.y)} r="2" fill={getRoomColor($config, p.room?.id)} fill-opacity={0.25 + 0.75 * (1 - p.age)} data-testid="history-point" />
	{/each}
	{#if ghost}
		<g data-testid="history-ghost">
			<circle cx={chart.xScale(ghost.x)} cy={chart.yScale(ghost.y)} r="6" fill={ghostColor} stroke="white" stroke-width="2" />
			<text x={chart.xScale(ghost.x) + 9} y={chart.yScale(ghost.y) + 3} fill="white" font-size="10px">{formatClock(view.cursor)}</text>
		</g>
	{/if}
</g>
