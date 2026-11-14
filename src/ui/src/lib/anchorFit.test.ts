import { describe, expect, it } from 'vitest';
import { anchorFitRows } from './anchorFit';
import type { OptimizerCycleReport } from './types';

const report = (over: Partial<OptimizerCycleReport> = {}): OptimizerCycleReport => ({
	at: '2026-11-14T19:00:00Z',
	dryRun: false,
	outcome: 'Applied',
	baselineAnchors: { anchors: 3, readings: 35, rms: 4.3955 },
	proposals: [{ optimizer: 'Per Node Absorption Rx Tx Adj', decision: 'Selected', applied: true, anchors: { anchors: 3, readings: 35, rms: 4.2396, better: 2, worse: 1 } }],
	...over
});

describe('anchorFitRows', () => {
	it('puts the current calibration first, then each proposal against it', () => {
		expect(anchorFitRows(report())).toEqual([
			{ label: 'Current', anchors: 3, rms: 4.3955 },
			{ label: 'Per Node Absorption Rx Tx Adj', status: 'applied', anchors: 3, rms: 4.2396, versus: 'better on 2, worse on 1' }
		]);
	});

	it('counts the anchors a proposal ties on', () => {
		const rows = anchorFitRows(report({ proposals: [{ optimizer: 'X', decision: 'NotBetter', applied: false, anchors: { anchors: 4, readings: 40, rms: 4, better: 1, worse: 1 } }] }));
		expect(rows?.[1]).toMatchObject({ status: 'not applied', versus: 'better on 1, worse on 1, same on 2' });
	});

	it('marks a picked proposal in a dry run as selected', () => {
		const rows = anchorFitRows(report({ dryRun: true, proposals: [{ optimizer: 'X', decision: 'Selected', applied: false, anchors: { anchors: 3, readings: 30, rms: 4 } }] }));
		expect(rows?.[1].status).toBe('selected');
	});

	it('is null when no anchor was heard by enough nodes', () => {
		expect(anchorFitRows(report({ baselineAnchors: undefined }))).toBeNull();
		expect(anchorFitRows(null)).toBeNull();
	});

	it('skips a proposal without an anchor fit', () => {
		expect(anchorFitRows(report({ proposals: [{ optimizer: 'X', decision: 'MinSamples', applied: false }] }))).toHaveLength(1);
	});
});
