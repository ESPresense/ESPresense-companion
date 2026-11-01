import { describe, expect, it } from 'vitest';
import { anchorSpot } from './calibrationSpot';

const ground = {
	id: 'ground',
	bounds: [
		[0, 0, 0],
		[10, 10, 3]
	]
};
const upstairs = {
	id: 'upstairs',
	bounds: [
		[0, 0, 3],
		[10, 10, 6]
	]
};
const floors = [ground, upstairs];

describe('anchorSpot', () => {
	it('keeps the saved height instead of a default', () => {
		expect(anchorSpot({ x: 12.6, y: 6.6, z: 2.15 }, floors)).toEqual({
			spot: { x: 12.6, y: 6.6, z: 2.15 },
			height: 2.15,
			floorId: 'ground'
		});
	});

	it('finds the floor from z and measures height from it', () => {
		const anchor = anchorSpot({ x: 1, y: 2, z: 4.7 }, floors);
		expect(anchor?.floorId).toBe('upstairs');
		expect(anchor?.height).toBe(1.7);
	});

	it('prefers the floor the device reports', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 3 }, floors, 'upstairs')?.floorId).toBe('upstairs');
	});

	it('ignores a reported floor that is not configured', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 4.7 }, floors, 'gone')?.floorId).toBe('upstairs');
	});

	it('tells apart floors at the same height by x and y', () => {
		const wing = {
			id: 'wing',
			bounds: [
				[20, 0, 0],
				[30, 10, 3]
			]
		};
		expect(anchorSpot({ x: 25, y: 5, z: 1 }, [ground, wing])?.floorId).toBe('wing');
	});

	it('takes the first floor containing a point on a shared boundary', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 3 }, floors)?.floorId).toBe('ground');
	});

	it('treats zero as a real coordinate', () => {
		expect(anchorSpot({ x: 0, y: 0, z: 0 }, floors)).toEqual({ spot: { x: 0, y: 0, z: 0 }, height: 0, floorId: 'ground' });
	});

	it('allows a device below the lowest floor', () => {
		const anchor = anchorSpot({ x: 1, y: 2, z: -0.5 }, floors);
		expect(anchor?.floorId).toBe('ground');
		expect(anchor?.height).toBe(-0.5);
	});

	it('uses the nearest floor for a device above them all', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 7 }, floors)?.floorId).toBe('upstairs');
	});

	it('is null unless every coordinate is set', () => {
		expect(anchorSpot({ x: 1, y: 2, z: null }, floors)).toBeNull();
		expect(anchorSpot({ x: 1, y: 2 }, floors)).toBeNull();
		expect(anchorSpot(null, floors)).toBeNull();
	});

	it('is null until there are floors to measure from', () => {
		expect(anchorSpot({ x: 1, y: 2, z: 1 }, undefined)).toBeNull();
		expect(anchorSpot({ x: 1, y: 2, z: 1 }, [])).toBeNull();
	});
});
