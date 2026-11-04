<script lang="ts">
	import { zoomIdentity } from 'd3-zoom';
	import { getLayerCakeContext } from 'layercake';
	import { calibration, devices, nodes } from '#lib/stores.js';
	import { errorColor } from '#lib/explain.js';
	import { anchorEnds, filterLinks, linkLabel, nodeLinks, type LinkDirection, type NodeLink } from '#lib/nodeLinks.js';
	import type { MapScales } from '#lib/types.js';

	const chart = getLayerCakeContext<MapScales>();

	interface Props {
		transform?: any;
		/** Only this node's links, each labelled. Without it, every link (thinned by `nearest`). */
		nodeId?: string | null;
		floorId?: string | null;
		nearest?: number | null;
		direction?: LinkDirection;
	}

	let { transform = zoomIdentity, nodeId = null, floorId = null, nearest = null, direction = 'all' }: Props = $props();

	let hovered: string | null = $state(null);

	let anchors = $derived(anchorEnds($devices, $calibration?.anchored));
	let all = $derived(nodeLinks($nodes, $calibration?.matrix, floorId, anchors));
	let links = $derived(filterLinks(all, { nodeId, nearest, direction }));
	let names = $derived(new Map([...anchors.map((a) => [a.id, a.name] as const), ...($nodes ?? []).map((n) => [n.id, n.name ?? n.id] as const)]));

	function title(l: NodeLink) {
		return l.readings.map((r) => `${names.get(r.tx)} → ${names.get(r.rx)}: ${linkLabel(r)} m`).join('\n');
	}
</script>

<g transform={transform.toString()} data-testid="node-pairs">
	{#each links as l (l.key)}
		{@const [x1, y1] = [chart.xScale(l.a.location.x), chart.yScale(l.a.location.y)]}
		{@const [x2, y2] = [chart.xScale(l.b.location.x), chart.yScale(l.b.location.y)]}
		{@const dim = hovered != null && hovered !== l.key}
		<g role="img" aria-label={title(l)} onmouseenter={() => (hovered = l.key)} onmouseleave={() => (hovered = null)} data-testid="node-link">
			<title>{title(l)}</title>
			<line {x1} {y1} {x2} {y2} stroke={errorColor(l.worst.percent)} stroke-width="2" stroke-opacity={dim ? 0.15 : 0.8} stroke-dasharray={l.a.anchor || l.b.anchor ? '6 3' : undefined} />
			<!-- A wider invisible line, so a 2px link is easy to hover -->
			<line {x1} {y1} {x2} {y2} stroke="transparent" stroke-width="10" />
			{#if nodeId || hovered === l.key}
				<text x={(x1 + x2) / 2} y={(y1 + y2) / 2} fill="white" font-size="10px" text-anchor="middle" pointer-events="none">{linkLabel(l.worst)}</text>
			{/if}
		</g>
	{/each}
</g>
