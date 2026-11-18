// Device markers and history trails in the 3D view.

/** Opacity a device marker keeps once it has gone quiet for its whole timeout. */
export const STALE_OPACITY = 0.3;

/**
 * How solid to draw a device last heard `ageMs` ago, given its timeout. A device reports every second or
 * two, so it stays solid for the first fifth of its timeout (at least 5 s), then fades steadily to
 * STALE_OPACITY at the timeout and stays there. An unknown age (never seen) counts as stale; a negative
 * one (the browser's clock behind the server's) as fresh.
 */
export function lastSeenOpacity(ageMs: number | null, timeoutMs: number | null | undefined): number {
	if (ageMs == null || !Number.isFinite(ageMs)) return STALE_OPACITY;
	const timeout = timeoutMs && timeoutMs > 0 ? timeoutMs : 30_000;
	const solidFor = Math.min(Math.max(timeout / 5, 5_000), timeout);
	if (ageMs <= solidFor) return 1;
	if (ageMs >= timeout) return STALE_OPACITY;
	return 1 - ((1 - STALE_OPACITY) * (ageMs - solidFor)) / (timeout - solidFor);
}

/** A point of a trail to draw: where it is, its colour (0-1 rgb) and how solid it is (0-1). */
export interface TubePoint {
	x: number;
	y: number;
	z: number;
	rgb: [number, number, number];
	alpha: number;
}

export interface TubeGeometry {
	/** xyz per vertex */
	positions: Float32Array;
	/** rgba per vertex */
	colors: Float32Array;
	/** three per triangle */
	indices: Uint32Array;
}

/**
 * A thin tube along each run of points, as one indexed triangle list. WebGL draws lines one pixel wide
 * whatever their width, so the trail is a tube instead: each segment is a prism of `sides` faces of
 * `radius`, coloured at each end by that end's point, so colour and opacity run along the path.
 * Segments of zero length are skipped; a run of fewer than two points draws nothing.
 */
export function trailTube(runs: TubePoint[][], radius: number, sides = 6): TubeGeometry {
	const positions: number[] = [];
	const colors: number[] = [];
	const indices: number[] = [];
	for (const run of runs) {
		for (let i = 1; i < run.length; i++) {
			const a = run[i - 1];
			const b = run[i];
			const dx = b.x - a.x;
			const dy = b.y - a.y;
			const dz = b.z - a.z;
			const length = Math.hypot(dx, dy, dz);
			if (length < 1e-6) continue;
			const axis = [dx / length, dy / length, dz / length];
			// Two unit vectors square to the segment and to each other: start from whichever world axis is least parallel
			const ref = Math.abs(axis[2]) < 0.9 ? [0, 0, 1] : [1, 0, 0];
			const u = normalize(cross(axis, ref));
			const v = cross(axis, u);
			const base = positions.length / 3;
			for (const end of [a, b]) {
				for (let s = 0; s < sides; s++) {
					const angle = (s / sides) * 2 * Math.PI;
					const c = Math.cos(angle) * radius;
					const n = Math.sin(angle) * radius;
					positions.push(end.x + u[0] * c + v[0] * n, end.y + u[1] * c + v[1] * n, end.z + u[2] * c + v[2] * n);
					colors.push(end.rgb[0], end.rgb[1], end.rgb[2], end.alpha);
				}
			}
			for (let s = 0; s < sides; s++) {
				const next = (s + 1) % sides;
				const a0 = base + s;
				const a1 = base + next;
				const b0 = base + sides + s;
				const b1 = base + sides + next;
				indices.push(a0, a1, b1, a0, b1, b0);
			}
		}
	}
	return { positions: new Float32Array(positions), colors: new Float32Array(colors), indices: new Uint32Array(indices) };
}

function cross(a: number[], b: number[]): number[] {
	return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}

function normalize(a: number[]): number[] {
	const l = Math.hypot(a[0], a[1], a[2]);
	return [a[0] / l, a[1] / l, a[2] / l];
}
