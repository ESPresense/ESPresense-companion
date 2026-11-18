import { describe, expect, it } from 'vitest';
import { lastSeenOpacity, STALE_OPACITY, trailTube, type TubePoint } from './devices3d';

describe('lastSeenOpacity', () => {
	it('is solid for a device heard just now, or with the browser clock behind', () => {
		expect(lastSeenOpacity(0, 30_000)).toBe(1);
		expect(lastSeenOpacity(5_999, 30_000)).toBe(1);
		expect(lastSeenOpacity(-2_000, 30_000)).toBe(1);
	});

	it('fades steadily to the stale opacity at the timeout, and stays there', () => {
		const mid = lastSeenOpacity(18_000, 30_000);
		expect(mid).toBeLessThan(1);
		expect(mid).toBeGreaterThan(STALE_OPACITY);
		expect(lastSeenOpacity(12_000, 30_000)).toBeGreaterThan(mid);
		expect(lastSeenOpacity(30_000, 30_000)).toBeCloseTo(STALE_OPACITY);
		expect(lastSeenOpacity(3_600_000, 30_000)).toBe(STALE_OPACITY);
	});

	it('stays solid at least 5 s however short the timeout, and never past it', () => {
		expect(lastSeenOpacity(4_000, 10_000)).toBe(1);
		expect(lastSeenOpacity(3_000, 3_000)).toBe(1);
		expect(lastSeenOpacity(3_001, 3_000)).toBe(STALE_OPACITY);
	});

	it('takes 30 s when the timeout is missing, and counts a device never seen as stale', () => {
		expect(lastSeenOpacity(30_000, undefined)).toBeCloseTo(STALE_OPACITY);
		expect(lastSeenOpacity(null, 30_000)).toBe(STALE_OPACITY);
		expect(lastSeenOpacity(NaN, 30_000)).toBe(STALE_OPACITY);
	});
});

describe('trailTube', () => {
	const p = (x: number, y: number, z: number, alpha = 1): TubePoint => ({ x, y, z, rgb: [1, 0.5, 0], alpha });

	it('builds a prism per segment, its ends coloured by their points', () => {
		const tube = trailTube([[p(0, 0, 1, 0.2), p(2, 0, 1, 0.9), p(2, 3, 1, 1)]], 0.05, 6);
		expect(tube.positions.length).toBe(2 * 2 * 6 * 3); // 2 segments × 2 rings × 6 vertices
		expect(tube.colors.length).toBe(2 * 2 * 6 * 4);
		expect(tube.indices.length).toBe(2 * 6 * 2 * 3); // 6 quads of 2 triangles each
		expect(tube.colors[3]).toBeCloseTo(0.2); // first ring takes the first point's alpha
		expect(tube.colors[6 * 4 + 3]).toBeCloseTo(0.9);
		expect(Math.max(...tube.indices)).toBe(tube.positions.length / 3 - 1);
	});

	it('puts every ring vertex the radius away from the axis, square to it', () => {
		for (const [a, b] of [
			[p(0, 0, 0), p(1, 1, 0)],
			[p(0, 0, 0), p(0, 0, 2)], // straight up: the other reference axis
			[p(1, 2, 3), p(-1, 0, 4)]
		]) {
			const tube = trailTube([[a, b]], 0.1, 8);
			for (let i = 0; i < 8; i++) {
				const off = [tube.positions[i * 3] - a.x, tube.positions[i * 3 + 1] - a.y, tube.positions[i * 3 + 2] - a.z];
				expect(Math.hypot(off[0], off[1], off[2])).toBeCloseTo(0.1);
				expect(off[0] * (b.x - a.x) + off[1] * (b.y - a.y) + off[2] * (b.z - a.z)).toBeCloseTo(0);
			}
		}
	});

	it('draws nothing for short runs or zero-length segments, and keeps runs apart', () => {
		expect(trailTube([[p(0, 0, 0)], []], 0.05).indices.length).toBe(0);
		expect(trailTube([[p(1, 1, 1), p(1, 1, 1)]], 0.05).indices.length).toBe(0);
		const two = trailTube(
			[
				[p(0, 0, 0), p(1, 0, 0)],
				[p(5, 0, 0), p(6, 0, 0)]
			],
			0.05,
			4
		);
		expect(two.positions.length).toBe(2 * 2 * 4 * 3); // no segment bridging (1,0,0) to (5,0,0)
	});
});
