import { describe, expect, it } from 'vitest';
import { anchorSpot } from './calibrationSpot';

describe('anchorSpot', () => {
	it('keeps the saved height instead of a default', () => {
		expect(anchorSpot({ x: 12.6, y: 6.6, z: 2.15 }, 0)).toEqual({
			spot: { x: 12.6, y: 6.6, z: 2.15 },
			height: 2.15
		});
	});

	it('measures height from the floor, not from zero', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 4.7 }, 3)?.height).toBeCloseTo(1.7);
	});

	it('treats zero as a real coordinate', () => {
		expect(anchorSpot({ x: 0, y: 0, z: 0 }, 0)).toEqual({ spot: { x: 0, y: 0, z: 0 }, height: 0 });
	});

	it('allows a device below the floor', () => {
		expect(anchorSpot({ x: 1, y: 2, z: -0.5 }, 0)?.height).toBe(-0.5);
	});

	it('is null unless every coordinate is set', () => {
		expect(anchorSpot({ x: 1, y: 2, z: null }, 0)).toBeNull();
		expect(anchorSpot({ x: 1, y: 2 }, 0)).toBeNull();
		expect(anchorSpot(null, 0)).toBeNull();
	});
});
