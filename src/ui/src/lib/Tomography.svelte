<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import { interpolateTurbo } from 'd3';
	import type { LayerCakeContext, TomographyFloor } from '$lib/types';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');

	export let tomo: TomographyFloor | null = null;
	export let transform = zoomIdentity;
	export let opacity = 0.65;

	interface Cell {
		x: number;
		y: number;
		w: number;
		h: number;
		fill: string;
		a: number;
	}

	// Project a cell (in metres) to screen-space, handling axis flips.
	function rect(cx: number, cy: number, size: number) {
		const sx0 = $xScale(cx),
			sx1 = $xScale(cx + size);
		const sy0 = $yScale(cy),
			sy1 = $yScale(cy + size);
		return {
			x: Math.min(sx0, sx1),
			y: Math.min(sy0, sy1),
			w: Math.abs(sx1 - sx0),
			h: Math.abs(sy1 - sy0)
		};
	}

	$: maxAtt = tomo?.maxAttenuation && tomo.maxAttenuation > 0 ? tomo.maxAttenuation : 1;
	$: maxCov = tomo ? Math.max(1, ...tomo.coverage) : 1;

	$: cells = (() => {
		if (!tomo) return [] as Cell[];
		const out: Cell[] = [];
		for (let row = 0; row < tomo.rows; row++) {
			for (let col = 0; col < tomo.cols; col++) {
				const idx = row * tomo.cols + col;
				const att = tomo.attenuation[idx] ?? 0;
				const cov = tomo.coverage[idx] ?? 0;
				if (att <= 0.05 || cov <= 0) continue;
				const cx = tomo.minX + col * tomo.cellSize;
				const cy = tomo.minY + row * tomo.cellSize;
				const intensity = Math.min(1, att / maxAtt);
				// Smoothness interpolates between links; keep interpolated cells visible but still
				// fade the lowest-coverage areas a little as a confidence cue (floored at 0.45).
				const conf = 0.45 + 0.55 * Math.min(1, cov / (0.25 * maxCov));
				const r = rect(cx, cy, tomo.cellSize);
				out.push({
					...r,
					fill: interpolateTurbo(0.15 + 0.85 * intensity),
					a: intensity * conf
				});
			}
		}
		return out;
	})();
</script>

{#if tomo}
	<g transform={transform.toString()} pointer-events="none">
		{#each cells as c}
			<rect x={c.x} y={c.y} width={c.w} height={c.h} fill={c.fill} fill-opacity={c.a * opacity} />
		{/each}
	</g>
{/if}
