import type { Device, Node, NodeCalibrationMatrix } from './types';

/** One end of a link: a placed node, or an anchored device (fixed at a known position, transmit only). */
export interface LinkEnd {
	/** Node id, or `anchor:` + device id so the two can't collide. */
	id: string;
	name: string;
	location: { x: number; y: number; z: number };
	floors?: string[] | null;
	anchor?: boolean;
}

/** One direction of a node-to-node link: `rx` measured `tx` at `measured` metres; the map says `map`. */
export interface LinkReading {
	tx: string;
	rx: string;
	measured: number;
	map: number;
	/** (measured − map) / map: below zero measures short, above zero measures long. */
	percent: number;
}

export interface NodeLink {
	/** Stable key for the pair, whatever the direction. */
	key: string;
	a: LinkEnd;
	b: LinkEnd;
	/** The direction that is further off. A node that only transmits badly can't average itself green. */
	worst: LinkReading;
	readings: LinkReading[];
}

export type LinkDirection = 'all' | 'long' | 'short';

/** How far off a reading is, the same for half and double the real distance. */
export function linkError(r: Pick<LinkReading, 'measured' | 'map'>): number {
	return Math.abs(Math.log(r.measured / r.map));
}

/** The anchored devices the calibration matrix lists, as link ends. */
export function anchorEnds(devices: Device[] | undefined, anchored: string[] | undefined): LinkEnd[] {
	const names = new Set(anchored ?? []);
	return (devices ?? []).filter((d) => d.isAnchored && d.location && names.has(d.name ?? d.id)).map((d) => ({ id: `anchor:${d.id}`, name: d.name ?? d.id, location: d.location, floors: d.floor?.id ? [d.floor.id] : null, anchor: true }));
}

/**
 * Pairs every two placed nodes that hear each other, one entry per pair, plus each anchored device
 * with the nodes that hear it. The calibration matrix is keyed by name (falling back to id),
 * transmitter first; rows for anything not passed in are skipped.
 */
export function nodeLinks(nodes: Node[] | undefined, matrix: NodeCalibrationMatrix | undefined, floorId: string | null = null, anchors: LinkEnd[] = []): NodeLink[] {
	const placed: LinkEnd[] = (nodes ?? []).flatMap((n) => (n?.location ? [{ id: n.id, name: n.name ?? n.id, location: n.location, floors: n.floors }] : []));
	// Nodes win a name clash, as they did before anchors were drawn
	const ends = [...anchors, ...placed].filter((e) => !floorId || e.floors?.includes(floorId));
	const byName = new Map(ends.map((e) => [e.name, e]));
	const links = new Map<string, NodeLink>();

	for (const [txName, row] of Object.entries(matrix ?? {})) {
		const tx = byName.get(txName);
		if (!tx) continue;
		for (const [rxName, cell] of Object.entries(row)) {
			const rx = byName.get(rxName);
			if (!rx || rx.anchor || rx.id === tx.id) continue;
			if (!(cell?.distance > 0) || !(cell?.mapDistance > 0)) continue;
			const reading: LinkReading = { tx: tx.id, rx: rx.id, measured: cell.distance, map: cell.mapDistance, percent: (cell.distance - cell.mapDistance) / cell.mapDistance };
			const [a, b] = tx.id < rx.id ? [tx, rx] : [rx, tx];
			const key = `${a.id}|${b.id}`;
			const link = links.get(key);
			if (!link) links.set(key, { key, a, b, worst: reading, readings: [reading] });
			else {
				link.readings.push(reading);
				if (linkError(reading) > linkError(link.worst)) link.worst = reading;
			}
		}
	}
	return [...links.values()];
}

function distance(a: LinkEnd, b: LinkEnd): number {
	return Math.hypot(a.location.x - b.location.x, a.location.y - b.location.y, a.location.z - b.location.z);
}

/**
 * Keeps a link when either end has the other among its `n` nearest linked neighbours. An anchor's
 * links count only toward the anchor's `n`, so anchors don't crowd out links between nodes.
 */
export function nearestLinks(links: NodeLink[], n: number): NodeLink[] {
	const neighbours = new Map<string, NodeLink[]>();
	for (const l of links) {
		for (const id of l.a.anchor ? [l.a.id] : l.b.anchor ? [l.b.id] : [l.a.id, l.b.id]) {
			const list = neighbours.get(id) ?? [];
			list.push(l);
			neighbours.set(id, list);
		}
	}
	const keep = new Set<string>();
	for (const list of neighbours.values()) {
		list
			.sort((x, y) => distance(x.a, x.b) - distance(y.a, y.b))
			.slice(0, n)
			.forEach((l) => keep.add(l.key));
	}
	return links.filter((l) => keep.has(l.key));
}

export function filterLinks(links: NodeLink[], { nodeId = null, nearest = null, direction = 'all' }: { nodeId?: string | null; nearest?: number | null; direction?: LinkDirection }): NodeLink[] {
	// One node isolated: all of its links, so the nearest-N thinning can't hide its bad ones
	let result = nodeId ? links.filter((l) => l.a.id === nodeId || l.b.id === nodeId) : nearest ? nearestLinks(links, nearest) : links;
	if (direction === 'long') result = result.filter((l) => l.worst.percent > 0);
	if (direction === 'short') result = result.filter((l) => l.worst.percent < 0);
	return result;
}

/** `measured (real)` in metres. */
export function linkLabel(r: Pick<LinkReading, 'measured' | 'map'>): string {
	return `${r.measured.toFixed(1)} (${r.map.toFixed(1)})`;
}
