<!--
  @component
  Generates an SVG x-axis. This component is also configured to detect if your x-scale is an ordinal scale. If so, it will place the markers in the middle of the bandwidth.
 -->
<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import type { Readable } from 'svelte/store';
	import type { ZoomScale } from 'd3-zoom';

	const context: {
		width: Readable<number>;
		height: Readable<number>;
		padding: Readable<{ top: number; right: number; bottom: number; left: number }>;
		yRange: Readable<number[]>;
		xScale: Readable<ZoomScale>;
		yScale: Readable<ZoomScale>;
	} = getContext('LayerCake');
	const { width, height, padding, yRange, xScale, yScale } = context;

	interface Props {
		transform?: any;
		/** Extend lines from the ticks into the chart space */
		gridlines?: boolean;
		/** Show a vertical mark for each tick. */
		tickMarks?: boolean;
		baseline?: boolean;
		/** Instead of centering the text on the first and the last items, align them to the edges of the chart. */
		snapTicks?: boolean;
		/** A function that passes the current tick value and expects a nicely formatted value in return. */
		formatTick?: (d: any) => any;
		/** If this is a number, it passes that along to the [d3Scale.ticks](https://github.com/d3/d3-scale) function. If this is an array, hardcodes the ticks to those values. If it's a function, passes along the default tick values and expects an array of tick values in return. If nothing, it uses the default ticks supplied by the D3 function. */
		ticks?: number | any[] | ((ticks: any[]) => any[]);
		/** TK */
		xTick?: number;
		/** The distance from the baseline to place each tick value. */
		yTick?: number;
		/** Any optional value passed to the `dx` attribute on the text marker and tick mark (if visible). This is ignored on the text marker if your scale is ordinal. */
		dxTick?: number;
		/** Any optional value passed to the `dy` attribute on the text marker and tick mark (if visible). This is ignored on the text marker if your scale is ordinal. */
		dyTick?: number;
	}

	let { transform = zoomIdentity, gridlines = false, tickMarks = true, baseline = true, snapTicks = false, formatTick = (d) => d, ticks = undefined, xTick = 0, yTick = -16, dxTick = 4, dyTick = 16 }: Props = $props();

	let x = $derived(transform.rescaleX($xScale));

	function textAnchor(i) {
		return 'start';
	}

	let isBandwidth = $derived(typeof x.bandwidth === 'function');
	let tickVals = $derived(Array.isArray(ticks) ? ticks : isBandwidth ? x.domain() : typeof ticks === 'function' ? ticks(x.ticks()) : x.ticks(ticks));
</script>

<g class="axis x-axis" class:snapTicks transform="translate(0, {$padding.bottom})">
	{#each tickVals as tick, i (tick)}
		<g class="tick tick-{i}" transform="translate({x(tick)},{Math.max(...$yRange)})">
			{#if gridlines !== false}
				<line class="gridline" y1={$height * -1} y2="0" x1="0" x2="0" />
			{/if}
			{#if tickMarks === true}
				<line class="tick-mark" y1={0} y2={-6} x1={xTick || isBandwidth ? x.bandwidth() / 2 : 0} x2={xTick || isBandwidth ? x.bandwidth() / 2 : 0} />
			{/if}
			<text x={xTick || isBandwidth ? x.bandwidth() / 2 : 0} y={yTick} dx={isBandwidth ? -9 : dxTick} dy={isBandwidth ? 4 : dyTick} text-anchor={textAnchor(i)}>{formatTick(tick)}</text>
		</g>
	{/each}
	{#if baseline === true}
		<line class="baseline" y1={$height + 0.5} y2={$height + 0.5} x1="0" x2={$width} />
	{/if}
</g>

<style>
	.tick {
		font-size: 0.725em;
		font-weight: 200;
	}

	line,
	.tick line {
		stroke: #aaa;
		stroke-dasharray: 2;
	}

	.tick text {
		fill: #666;
	}

	.tick .tick-mark,
	.baseline {
		stroke-dasharray: 0;
	}
</style>
