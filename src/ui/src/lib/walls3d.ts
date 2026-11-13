// Wall segments for the 3D view: one per room edge, with an edge two rooms share drawn once.
import type { Room } from './types';

export type WallSegment = {
	a: [number, number];
	b: [number, number];
	/** The first room, in config order, that has this edge: the wall takes its colour when no wall_color is set. */
	roomId: string;
};

/** Walls stop this far up the storey (dollhouse style), so an oblique camera sees into every room. */
export const WALL_CUT_FRACTION = 1 / 3;
/** wall_thickness: 0 is a hairline on the 2D map; in 3D it still needs some body to be seen. */
export const MIN_WALL_THICKNESS = 0.02;

const ON_EDGE = 0.001; // metres: how close a corner must be to an edge to cut it
const key = (p: [number, number]) => `${Math.round(p[0] * 1000)},${Math.round(p[1] * 1000)}`;

type Edge = { a: [number, number]; b: [number, number]; roomId: string };

/**
 * The walls of one floor: every room's outline, cut wherever another outline's corner lands on it, with
 * each resulting piece kept once. So a wall two rooms share is one wall, not two on top of each other,
 * even when a long edge of one room runs past two shorter rooms. Points match to the millimetre.
 * Zero-length edges are dropped, which also covers an outline whose last point repeats its first.
 */
export function wallSegments(rooms: Room[]): WallSegment[] {
	const edges: Edge[] = [];
	const corners = new Map<string, [number, number]>();
	for (const room of rooms) {
		const points = room.points ?? [];
		if (points.length < 2) continue;
		for (let i = 0; i < points.length; i++) {
			const a = points[i];
			const b = points[(i + 1) % points.length];
			corners.set(key(a), [a[0], a[1]]);
			if (key(a) !== key(b)) edges.push({ a: [a[0], a[1]], b: [b[0], b[1]], roomId: room.id });
		}
	}

	const seen = new Set<string>();
	const segments: WallSegment[] = [];
	for (const edge of edges) {
		const dx = edge.b[0] - edge.a[0];
		const dy = edge.b[1] - edge.a[1];
		const length = Math.hypot(dx, dy);
		// Corners of any outline that lie on this edge, as distances along it
		const cuts: { t: number; p: [number, number] }[] = [
			{ t: 0, p: edge.a },
			{ t: length, p: edge.b }
		];
		for (const p of corners.values()) {
			const t = ((p[0] - edge.a[0]) * dx + (p[1] - edge.a[1]) * dy) / length;
			if (t <= ON_EDGE || t >= length - ON_EDGE) continue;
			const off = Math.abs((p[0] - edge.a[0]) * dy - (p[1] - edge.a[1]) * dx) / length;
			if (off < ON_EDGE) cuts.push({ t, p });
		}
		cuts.sort((m, n) => m.t - n.t);
		for (let i = 0; i + 1 < cuts.length; i++) {
			const ka = key(cuts[i].p);
			const kb = key(cuts[i + 1].p);
			if (ka === kb) continue;
			const id = ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`;
			if (seen.has(id)) continue;
			seen.add(id);
			segments.push({ a: cuts[i].p, b: cuts[i + 1].p, roomId: edge.roomId });
		}
	}
	return segments;
}

export type WallPost = { at: [number, number]; roomId: string };

/**
 * One post per wall end. The 3D view stops each wall half a thickness short of its ends and stands a
 * square post there, so walls meeting at a corner or a T never overlap (translucent walls would show
 * the overlap as a darker patch).
 */
export function wallPosts(segments: WallSegment[]): WallPost[] {
	const posts = new Map<string, WallPost>();
	for (const s of segments) {
		for (const at of [s.a, s.b]) if (!posts.has(key(at))) posts.set(key(at), { at, roomId: s.roomId });
	}
	return [...posts.values()];
}

/** How high the cut-away walls of a storey stand above its floor. */
export function wallHeight(floorBase: number, floorCeiling: number): number {
	return Math.max(0, floorCeiling - floorBase) * WALL_CUT_FRACTION;
}
