<!--
  @component
  Generates an HTML y-axis.
 -->
<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import type { Readable } from 'svelte/store';
	import type { LayerCakeContext } from '$lib/types';

	/** LayerCake scales are linear here, but ordinal (band) scales are detected at runtime. */
	type AxisScale = LayerCakeContext['yScale'] extends Readable<infer S> ? S & { bandwidth?: () => number } : never;
	type Ticks = number | number[] | ((defaultTicks: number[]) => number[]) | undefined;

	const context: Pick<LayerCakeContext, 'padding' | 'xScale'> & {
		xRange: Readable<number[]>;
		yScale: Readable<AxisScale>;
	} = getContext('LayerCake');
	const { padding, xRange, yScale } = context;

	interface Props {
		/** The current d3-zoom transform; the y-scale is rescaled through it so the axis follows pan/zoom. */
		transform?: typeof zoomIdentity;
		/** Extend lines from the ticks into the chart space */
		gridlines?: boolean;
		/** Show a vertical mark for each tick. */
		tickMarks?: boolean;
		/** A function that passes the current tick value and expects a nicely formatted value in return. */
		formatTick?: (d: number) => string | number;
		/** If this is a number, it passes that along to the [d3Scale.ticks](https://github.com/d3/d3-scale) function. If this is an array, hardcodes the ticks to those values. If it's a function, passes along the default tick values and expects an array of tick values in return. */
		ticks?: Ticks;
		/** How far over to position the text marker. */
		xTick?: number;
		/** How far up and down to position the text marker. */
		yTick?: number;
		/** Any optional value passed to the `dx` attribute on the text marker and tick mark (if visible). This is ignored on the text marker if your scale is ordinal. */
		dxTick?: number;
		/** Any optional value passed to the `dy` attribute on the text marker and tick mark (if visible). This is ignored on the text marker if your scale is ordinal. */
		dyTick?: number;
		textAnchor?: string;
	}

	let { transform = zoomIdentity, gridlines = false, tickMarks = true, formatTick = (d) => d, ticks = undefined, xTick = 0, yTick = 0, dxTick = 0, dyTick = -4, textAnchor = 'start' }: Props = $props();

	let y = $derived(transform.rescaleY($yScale));

	let isBandwidth = $derived(typeof y.bandwidth === 'function');
	/** Half the band width, to centre marks within the band on ordinal scales; 0 otherwise. */
	let halfBand = $derived(y.bandwidth ? y.bandwidth() / 2 : 0);
	let tickVals = $derived(Array.isArray(ticks) ? ticks : isBandwidth ? y.domain() : typeof ticks === 'function' ? ticks(y.ticks()) : y.ticks(ticks));
</script>

<g class="axis y-axis" transform="translate({-$padding.left}, 0)">
	{#each tickVals as tick (tick)}
		<g class="tick tick-{tick}" transform="translate({$xRange[0] + (isBandwidth ? $padding.left : 0)}, {y(tick)})">
			{#if gridlines !== false}
				<line class="gridline" x2="100%" y1={yTick + (isBandwidth ? halfBand : 0)} y2={yTick + (isBandwidth ? halfBand : 0)}></line>
			{/if}
			{#if tickMarks === true}
				<line class="tick-mark" x1="0" x2={isBandwidth ? -6 : 6} y1={yTick + (isBandwidth ? halfBand : 0)} y2={yTick + (isBandwidth ? halfBand : 0)}></line>
			{/if}
			<text x={xTick} y={yTick + (isBandwidth ? halfBand : 0)} dx={isBandwidth ? -9 : dxTick} dy={isBandwidth ? 4 : dyTick} style="text-anchor:{isBandwidth ? 'end' : textAnchor};">{formatTick(tick)}</text>
		</g>
	{/each}
</g>

<style>
	.tick {
		font-size: 0.725em;
		font-weight: 200;
	}

	.tick line {
		stroke: #aaa;
	}
	.tick .gridline {
		stroke-dasharray: 2;
	}

	.tick text {
		fill: #666;
	}

	.tick.tick-0 line {
		stroke-dasharray: 0;
	}
</style>
