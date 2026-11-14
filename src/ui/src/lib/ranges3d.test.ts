import { describe, expect, it } from 'vitest';
import { floorRingRadius, nodeRangeColors } from './ranges3d';

describe('floorRingRadius', () => {
	it('is the distance itself for a node on the floor', () => {
		expect(floorRingRadius(3, 0)).toBeCloseTo(3);
	});

	it('shrinks with the node height, above or below the floor', () => {
		expect(floorRingRadius(5, 3)).toBeCloseTo(4);
		expect(floorRingRadius(5, -3)).toBeCloseTo(4);
	});

	it('is null when the sphere does not reach the floor, or there is no distance', () => {
		expect(floorRingRadius(1, 2)).toBeNull();
		expect(floorRingRadius(2, 2)).toBeNull();
		expect(floorRingRadius(0, 0)).toBeNull();
		expect(floorRingRadius(NaN, 0)).toBeNull();
	});
});

describe('nodeRangeColors', () => {
	it('gives each node a colour that does not depend on the order they arrive in', () => {
		const a = nodeRangeColors(['kitchen', 'attic', 'hall']);
		const b = nodeRangeColors(['hall', 'kitchen', 'attic', 'hall']);
		expect(a.size).toBe(3);
		expect([...a]).toEqual([...b]);
		expect(new Set(a.values()).size).toBe(3);
	});

	it('keeps every node distinct however many there are', () => {
		const ids = Array.from({ length: 25 }, (_, i) => 'n' + String(i).padStart(2, '0'));
		expect(new Set(nodeRangeColors(ids).values()).size).toBe(25);
	});
});
