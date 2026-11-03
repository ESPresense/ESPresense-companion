import { describe, expect, it } from 'vitest';
import { bestScenario, ellipsePoints, errorColor, rejectionLabel } from './explain';
import type { DeviceExplanation, ExplainedScenario } from './types';

describe('errorColor', () => {
	it('is green when a distance is right', () => {
		expect(errorColor(0)).toBe('hsl(120, 50%, 50%)');
	});

	it('runs from blue when short to red when long and saturates', () => {
		expect(errorColor(-1)).toBe('hsl(240, 50%, 50%)');
		expect(errorColor(1)).toBe('hsl(0, 50%, 50%)');
		expect(errorColor(5)).toBe(errorColor(1));
	});
});

describe('ellipsePoints', () => {
	const ellipse = { center: { x: 1, y: 2, z: 0 }, semiMajor: 2, semiMinor: 1, angleDegrees: 90 };

	it('points the major axis along the angle', () => {
		const points = ellipsePoints(ellipse, 1, 4);
		expect(points[0][0]).toBeCloseTo(1);
		expect(points[0][1]).toBeCloseTo(4);
		expect(points[1][0]).toBeCloseTo(0);
		expect(points[1][1]).toBeCloseTo(2);
	});

	it('scales from one standard deviation', () => {
		expect(ellipsePoints(ellipse, 2, 4)[0][1]).toBeCloseTo(6);
	});
});

describe('bestScenario', () => {
	it('finds the winner and tolerates no explanation', () => {
		const scenarios = [{ best: false }, { best: true, name: 'winner' }] as ExplainedScenario[];
		expect(bestScenario({ scenarios } as DeviceExplanation)?.name).toBe('winner');
		expect(bestScenario(null)).toBeUndefined();
	});
});

describe('rejectionLabel', () => {
	it('falls back for reasons it does not know', () => {
		expect(rejectionLabel('Stale')).toBe('stale');
		expect(rejectionLabel('Other')).toBe('not used');
		expect(rejectionLabel(null)).toBe('not used');
	});
});
