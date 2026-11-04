import { describe, expect, it } from 'vitest';
import { anchorEnds, filterLinks, linkError, linkLabel, nearestLinks, nodeLinks } from './nodeLinks';
import type { Device, Node, NodeCalibrationMatrix } from './types';

const node = (id: string, x: number, y = 0, floors = ['1']) => ({ id, name: id.toUpperCase(), location: { x, y, z: 1 }, floors }) as unknown as Node;
const cell = (distance: number, mapDistance: number) => ({ distance, mapDistance }) as NodeCalibrationMatrix[string][string];

describe('nodeLinks', () => {
	const nodes = [node('a', 0), node('b', 4), node('c', 10), node('d', 20, 0, ['2']), { id: 'unplaced', name: 'UNPLACED' } as Node];

	it('makes one link per pair, coloured by the worse direction', () => {
		// B hears A at double the real distance, A hears B fine: the link is as bad as its worse side
		const links = nodeLinks(nodes, { A: { B: cell(8, 4) }, B: { A: cell(4.2, 4) } });
		expect(links).toHaveLength(1);
		expect(links[0].readings).toHaveLength(2);
		expect(links[0].worst).toMatchObject({ tx: 'a', rx: 'b', measured: 8, percent: 1 });
	});

	it('weighs half and double the real distance the same', () => {
		expect(linkError({ measured: 2, map: 4 })).toBeCloseTo(linkError({ measured: 8, map: 4 }));
		const links = nodeLinks(nodes, { A: { B: cell(2.1, 4) }, B: { A: cell(7, 4) } });
		expect(links[0].worst.measured).toBe(2.1);
	});

	it('skips anchors not passed in, unplaced nodes, missing distances and other floors', () => {
		const matrix = { A: { B: cell(0, 4), UNPLACED: cell(3, 3), D: cell(20, 20) }, 'Some Anchor': { A: cell(1, 1) } };
		expect(nodeLinks(nodes, matrix)).toHaveLength(1);
		expect(nodeLinks(nodes, matrix, '1')).toHaveLength(0);
	});
});

describe('anchored devices', () => {
	const nodes = [node('a', 0), node('b', 4)];
	const devices = [
		{ id: 'tv', name: 'Family TV', isAnchored: true, location: { x: 2, y: 3, z: 1 }, floor: { id: '1', name: 'Ground' } },
		{ id: 'phone', name: 'Phone', isAnchored: false, location: { x: 1, y: 1, z: 1 }, floor: { id: '1', name: 'Ground' } },
		{ id: 'tag', name: 'Upstairs Tag', isAnchored: true, location: { x: 1, y: 1, z: 4 }, floor: { id: '2', name: 'Up' } }
	] as unknown as Device[];
	const anchors = anchorEnds(devices, ['Family TV', 'Phone', 'Upstairs Tag']);

	it('takes only anchored devices the matrix lists', () => {
		expect(anchors.map((a) => a.id)).toEqual(['anchor:tv', 'anchor:tag']);
		expect(anchorEnds(devices, ['Family TV'])).toHaveLength(1);
	});

	it('links an anchor to each node that hears it, on its floor', () => {
		const matrix = { 'Family TV': { A: cell(3, 3.6), B: cell(5, 3.6) }, 'Upstairs Tag': { A: cell(4, 3) }, A: { 'Family TV': cell(1, 1) } };
		const links = nodeLinks(nodes, matrix, '1', anchors);
		expect(links.map((l) => l.key).sort()).toEqual(['anchor:tv|b', 'a|anchor:tv']);
		expect(links.every((l) => l.a.anchor || l.b.anchor)).toBe(true);
		expect(links.find((l) => l.key === 'anchor:tv|b')?.worst.tx).toBe('anchor:tv');
	});

	it("doesn't let anchors crowd out a node's nearest node links", () => {
		const matrix = { 'Family TV': { A: cell(2, 2), B: cell(2, 2) }, A: { B: cell(4, 4) } };
		const keys = nearestLinks(nodeLinks(nodes, matrix, null, anchors), 1).map((l) => l.key);
		expect(keys).toContain('a|b');
		expect(keys).toHaveLength(2);
	});
});

describe('filterLinks', () => {
	const nodes = [node('a', 0), node('b', 1), node('c', 2), node('d', 10)];
	const matrix = { A: { B: cell(1.5, 1), C: cell(1, 2), D: cell(10, 10) }, B: { C: cell(1, 1), D: cell(9, 9) }, C: { D: cell(8, 8) } };
	const links = nodeLinks(nodes, matrix);

	it('keeps a link when either end has the other among its nearest', () => {
		const keys = nearestLinks(links, 1)
			.map((l) => l.key)
			.sort();
		// a→b, b→a (or c), c→b, d→c
		expect(keys).toEqual(['a|b', 'b|c', 'c|d']);
	});

	it('isolates one node with all of its links, ignoring nearest', () => {
		expect(filterLinks(links, { nodeId: 'd', nearest: 1 })).toHaveLength(3);
	});

	it('filters to links measuring long or short', () => {
		expect(filterLinks(links, { direction: 'long' }).map((l) => l.key)).toEqual(['a|b']);
		expect(filterLinks(links, { direction: 'short' }).map((l) => l.key)).toEqual(['a|c']);
	});
});

describe('linkLabel', () => {
	it('reads measured (real)', () => {
		expect(linkLabel({ measured: 4.04, map: 3.16 })).toBe('4.0 (3.2)');
	});
});
