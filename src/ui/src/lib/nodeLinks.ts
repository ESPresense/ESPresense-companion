import type { Node, NodeCalibrationMatrix } from './types';

type Located = Pick<Node, 'id' | 'name' | 'floors'> & { location: { x: number; y: number; z: number } };

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
	a: Located;
	b: Located;
	/** The direction that is further off. A node that only transmits badly can't average itself green. */
	worst: LinkReading;
	readings: LinkReading[];
}

export type LinkDirection = 'all' | 'long' | 'short';

/** How far off a reading is, the same for half and double the real distance. */
export function linkError(r: Pick<LinkReading, 'measured' | 'map'>): number {
	return Math.abs(Math.log(r.measured / r.map));
}

function hasLocation(n: Node | undefined): n is Node & Located {
	return !!n?.location;
}

/**
 * Pairs every two placed nodes that hear each other, one entry per pair. The calibration matrix is
 * keyed by node name (falling back to id), transmitter first; anchored devices in it are skipped.
 */
export function nodeLinks(nodes: Node[] | undefined, matrix: NodeCalibrationMatrix | undefined, floorId: string | null = null): NodeLink[] {
	const placed = (nodes ?? []).filter(hasLocation).filter((n) => !floorId || n.floors?.includes(floorId));
	const byName = new Map(placed.map((n) => [n.name ?? n.id, n]));
	const links = new Map<string, NodeLink>();

	for (const [txName, row] of Object.entries(matrix ?? {})) {
		const tx = byName.get(txName);
		if (!tx) continue;
		for (const [rxName, cell] of Object.entries(row)) {
			const rx = byName.get(rxName);
			if (!rx || rx.id === tx.id) continue;
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

function distance(a: Located, b: Located): number {
	return Math.hypot(a.location.x - b.location.x, a.location.y - b.location.y, a.location.z - b.location.z);
}

/** Keeps a link when either end has the other among its `n` nearest linked neighbours. */
export function nearestLinks(links: NodeLink[], n: number): NodeLink[] {
	const neighbours = new Map<string, NodeLink[]>();
	for (const l of links) {
		for (const id of [l.a.id, l.b.id]) {
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
