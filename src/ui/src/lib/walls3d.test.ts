import { describe, expect, it } from 'vitest';
import { wallHeight, wallOpacityAt, wallPosts, wallSegments } from './walls3d';
import type { Room } from './types';

const room = (id: string, points: [number, number][]): Room => ({ id, name: id, points });

describe('wallSegments', () => {
	it('gives one wall per edge and closes the outline', () => {
		const walls = wallSegments([
			room('a', [
				[0, 0],
				[4, 0],
				[4, 3],
				[0, 3]
			])
		]);
		expect(walls).toHaveLength(4);
		expect(walls[3]).toEqual({ a: [0, 3], b: [0, 0], roomId: 'a' });
	});

	it('draws a wall two rooms share once, whichever way each room winds', () => {
		const walls = wallSegments([
			room('a', [
				[0, 0],
				[4, 0],
				[4, 3],
				[0, 3]
			]),
			// Shares the x=4 edge with a, listed in the opposite direction and off by a float hair
			room('b', [
				[4.0000001, 3],
				[4, 0],
				[8, 0],
				[8, 3]
			])
		]);
		expect(walls).toHaveLength(7);
		expect(walls.filter((w) => w.a[0] === 4 && w.b[0] === 4)).toEqual([{ a: [4, 0], b: [4, 3], roomId: 'a' }]);
	});

	it('treats corners within a millimetre as one, even across a rounding boundary', () => {
		const walls = wallSegments([
			room('a', [
				[0, 0],
				[4.0004, 0],
				[4.0004, 3],
				[0, 3]
			]),
			room('b', [
				[4.0006, 0],
				[8, 0],
				[8, 3],
				[4.0006, 3]
			])
		]);
		expect(walls).toHaveLength(7);
		expect(walls.filter((w) => w.a[0] > 3.9 && w.a[0] < 4.1 && w.b[0] > 3.9 && w.b[0] < 4.1)).toHaveLength(1);
	});

	it('skips zero-length edges, such as an outline that repeats its first point', () => {
		const walls = wallSegments([
			room('a', [
				[0, 0],
				[4, 0],
				[4, 3],
				[0, 0]
			])
		]);
		expect(walls).toHaveLength(3);
	});

	it('cuts a long edge where shorter neighbours meet it, so no stretch is walled twice', () => {
		const walls = wallSegments([
			room('long', [
				[4, 0],
				[8, 0],
				[8, 9],
				[4, 9]
			]),
			room('top', [
				[0, 0],
				[4, 0],
				[4, 4],
				[0, 4]
			]),
			room('bottom', [
				[0, 4],
				[4, 4],
				[4, 9],
				[0, 9]
			])
		]);
		const onSharedLine = walls.filter((w) => w.a[0] === 4 && w.b[0] === 4);
		expect(onSharedLine.map((w) => [w.a[1], w.b[1]].sort((m, n) => m - n))).toEqual([
			[4, 9],
			[0, 4]
		]);
		expect(onSharedLine.every((w) => w.roomId === 'long')).toBe(true);
		// 4 + 4 + 4 outline edges, the long one cut in two, minus the two stretches and the y=4 edge shared
		expect(walls).toHaveLength(10);
	});

	it('ignores rooms without an outline', () => {
		expect(wallSegments([room('a', []), room('b', [[1, 1]])])).toEqual([]);
	});
});

describe('wallPosts', () => {
	it('stands one post at each wall end, however many walls meet there', () => {
		const walls = wallSegments([
			room('a', [
				[0, 0],
				[4, 0],
				[4, 3],
				[0, 3]
			]),
			room('b', [
				[4, 0],
				[8, 0],
				[8, 3],
				[4, 3]
			])
		]);
		const posts = wallPosts(walls);
		expect(posts).toHaveLength(6);
		expect(posts.find((p) => p.at[0] === 8 && p.at[1] === 0)?.roomId).toBe('b');
	});
});

describe('wallHeight', () => {
	it('cuts the walls at a third of the storey', () => {
		expect(wallHeight(3, 6)).toBeCloseTo(1);
		expect(wallHeight(0, 2.4)).toBeCloseTo(0.8);
	});

	it('is never negative', () => {
		expect(wallHeight(3, 0)).toBe(0);
	});
});

describe('wallOpacityAt', () => {
	it('runs from the floor opacity to the top opacity', () => {
		expect(wallOpacityAt(0, 0.35, 0)).toBeCloseTo(0.35);
		expect(wallOpacityAt(1, 0.35, 0)).toBeCloseTo(0);
		expect(wallOpacityAt(1, 0.35, 0.1)).toBeCloseTo(0.1);
	});

	it('is already past halfway to the top opacity at mid-height', () => {
		const mid = wallOpacityAt(0.5, 0.4, 0);
		expect(mid).toBeGreaterThan(0);
		expect(mid).toBeLessThan(0.2);
	});

	it('does not fade when both ends are the same', () => {
		for (const up of [0, 0.3, 1]) expect(wallOpacityAt(up, 0.5, 0.5)).toBeCloseTo(0.5);
	});

	it('can grow towards the ceiling, and clamps heights outside the storey', () => {
		expect(wallOpacityAt(0.5, 0, 0.6)).toBeGreaterThan(0.3);
		expect(wallOpacityAt(-1, 0.35, 0)).toBeCloseTo(0.35);
		expect(wallOpacityAt(2, 0.35, 0)).toBeCloseTo(0);
	});
});
