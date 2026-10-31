// Helpers for showing how a device's position was reached: see DeviceExplanation in the backend

import type { DeviceExplanation, ExplainedEllipse, ExplainedScenario } from './types';

/** 95% of a two-dimensional normal distribution falls within this many standard deviations */
export const ELLIPSE_95_PERCENT = 2.448;

/**
 * Colour for a relative distance error: blue reads short, green is right, red reads long.
 * Saturates at ±100%.
 */
export function errorColor(percent: number): string {
	const clamped = Math.max(-1, Math.min(percent, 1));
	return `hsl(${240 - (clamped + 1) * 120}, 50%, 50%)`;
}

export function bestScenario(explanation: DeviceExplanation | null | undefined): ExplainedScenario | undefined {
	return explanation?.scenarios.find((s) => s.best);
}

/**
 * Outline of the uncertainty ellipse in map coordinates. Points rather than an SVG ellipse, so the
 * caller's scales can apply flipped axes.
 */
export function ellipsePoints(ellipse: ExplainedEllipse, scale = ELLIPSE_95_PERCENT, steps = 48): Array<[number, number]> {
	const angle = (ellipse.angleDegrees * Math.PI) / 180;
	const [a, b] = [ellipse.semiMajor * scale, ellipse.semiMinor * scale];
	return Array.from({ length: steps }, (_, i) => {
		const t = (2 * Math.PI * i) / steps;
		const [u, v] = [a * Math.cos(t), b * Math.sin(t)];
		return [ellipse.center.x + u * Math.cos(angle) - v * Math.sin(angle), ellipse.center.y + u * Math.sin(angle) + v * Math.cos(angle)];
	});
}

const REJECTION_LABELS: Record<string, string> = {
	Stale: 'stale',
	NotOnFloor: 'not on this floor',
	OverMaxDistance: 'over max_distance',
	NotNearest: 'not the nearest',
	NotUsed: 'not used'
};

export function rejectionLabel(reason: string | null): string {
	return (reason && REJECTION_LABELS[reason]) ?? 'not used';
}
