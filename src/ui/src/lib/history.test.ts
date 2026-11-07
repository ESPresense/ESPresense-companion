import { describe, expect, it } from 'vitest';
import { advance, fixAt, floorAt, isOtherDay, mergeFixes, parseTimeSpan, retentionMs, roomAt, sampleEverySeconds, toFixes, trailBefore, viewAt, type HistoryFix } from './history';
import type { Config, DeviceHistory } from './types';

const T0 = Date.parse('2026-11-07T10:00:00Z');
const fix = (seconds: number, x = 0, y = 0, z: number | null = 1): HistoryFix => ({ t: T0 + seconds * 1000, x, y, z, confidence: 5, scenario: 'MultiFloor' });
const row = (seconds: number, extra: Partial<DeviceHistory> = {}): DeviceHistory => ({ id: 'dev-1', when: new Date(T0 + seconds * 1000).toISOString(), x: 1, y: 2, z: 1, confidence: 5, fixes: 3, scenario: 'MultiFloor', best: true, ...extra });

const config = {
	floors: [
		{
			id: 'first',
			name: 'First',
			bounds: [
				[0, 0, 0],
				[10, 10, 3]
			],
			rooms: [
				{
					id: 'kitchen',
					name: 'Kitchen',
					points: [
						[0, 0],
						[5, 0],
						[5, 5],
						[0, 5]
					]
				},
				{
					id: 'den',
					name: 'Den',
					points: [
						[5, 0],
						[10, 0],
						[10, 5],
						[5, 5]
					]
				}
			]
		},
		{
			id: 'second',
			name: 'Second',
			bounds: [
				[0, 0, 3],
				[10, 10, 6]
			],
			rooms: [
				{
					id: 'office',
					name: 'Office',
					points: [
						[0, 0],
						[10, 0],
						[10, 10],
						[0, 10]
					]
				}
			]
		}
	],
	history: { enabled: true, expireAfter: '12h', expireAfterTimeSpan: '12:00:00' }
} as unknown as Config;

describe('parseTimeSpan', () => {
	it('reads days, hours, minutes, seconds and fractions', () => {
		expect(parseTimeSpan('1.00:00:00')).toBe(86_400_000);
		expect(parseTimeSpan('12:30:00')).toBe(45_000_000);
		expect(parseTimeSpan('00:00:05.5')).toBe(5500);
		expect(parseTimeSpan('-00:01:00')).toBe(-60_000);
	});

	it('is null for anything else', () => {
		expect(parseTimeSpan('24h')).toBeNull();
		expect(parseTimeSpan('')).toBeNull();
		expect(parseTimeSpan(undefined)).toBeNull();
	});
});

describe('retentionMs', () => {
	it('comes from history.expire_after', () => {
		expect(retentionMs(config)).toBe(12 * 3_600_000);
	});

	it('is a day when the config has none', () => {
		expect(retentionMs(null)).toBe(86_400_000);
		expect(retentionMs({ history: { expireAfterTimeSpan: 'soon' } } as unknown as Config)).toBe(86_400_000);
	});
});

describe('sampleEverySeconds', () => {
	it('spreads a window over at most the point budget', () => {
		expect(sampleEverySeconds(86_400_000)).toBe(9);
		expect(sampleEverySeconds(3_600_000)).toBe(1);
		expect(sampleEverySeconds(10_000, 10)).toBe(1);
	});
});

describe('toFixes', () => {
	it('keeps the best rows only, oldest first, with a parsed time', () => {
		const fixes = toFixes([row(10), row(5, { best: false, x: 99 }), row(0)]);
		expect(fixes.map((f) => f.t)).toEqual([T0, T0 + 10_000]);
		expect(fixes[0].x).toBe(1);
	});

	it('uses every row when none is marked best', () => {
		expect(toFixes([row(1, { best: false }), row(0, { best: false })])).toHaveLength(2);
	});

	it('drops rows without a position or a readable time', () => {
		expect(toFixes([row(0, { x: null }), row(1, { when: 'never' }), row(2)])).toHaveLength(1);
	});
});

describe('mergeFixes', () => {
	it('adds the new fixes, keeps one per instant and forgets what fell out of the window', () => {
		const merged = mergeFixes([fix(0), fix(10)], [fix(10, 7), fix(20)], T0 + 5000);
		expect(merged.map((f) => f.t)).toEqual([T0 + 10_000, T0 + 20_000]);
		expect(merged[0].x).toBe(7);
	});
});

describe('fixAt', () => {
	const fixes = [fix(0, 1), fix(10, 2), fix(20, 3)];

	it('is the newest fix at or before the time', () => {
		expect(fixAt(fixes, T0 + 10_000)?.x).toBe(2);
		expect(fixAt(fixes, T0 + 15_000)?.x).toBe(2);
		expect(fixAt(fixes, T0 + 25_000)?.x).toBe(3);
	});

	it('is null before the first fix', () => {
		expect(fixAt(fixes, T0 - 1)).toBeNull();
	});

	it('is null once the last fix has gone stale', () => {
		expect(fixAt(fixes, T0 + 20_000 + 5 * 60_000)).not.toBeNull();
		expect(fixAt(fixes, T0 + 20_000 + 5 * 60_000 + 1)).toBeNull();
		expect(fixAt(fixes, T0 + 31_000, 10_000)).toBeNull();
	});
});

describe('trailBefore', () => {
	const fixes = [fix(0), fix(10), fix(20), fix(30)];

	it('is the window up to the time, oldest first, aged towards the window start', () => {
		const trail = trailBefore(fixes, T0 + 25_000, 20_000);
		expect(trail.map((p) => p.fix.t)).toEqual([T0 + 10_000, T0 + 20_000]);
		expect(trail.map((p) => p.age)).toEqual([0.75, 0.25]);
	});

	it('is empty without a window or before the first fix', () => {
		expect(trailBefore(fixes, T0 + 25_000, 0)).toEqual([]);
		expect(trailBefore(fixes, T0 - 1, 60_000)).toEqual([]);
	});
});

describe('floorAt and roomAt', () => {
	it('places a height on its floor and a point in its room', () => {
		expect(floorAt(config, 1)?.id).toBe('first');
		expect(floorAt(config, 4)?.id).toBe('second');
		expect(roomAt(floorAt(config, 1), 2, 2)?.id).toBe('kitchen');
		expect(roomAt(floorAt(config, 1), 7, 2)?.id).toBe('den');
		expect(roomAt(floorAt(config, 1), 7, 8)).toBeNull();
	});

	it('falls back to the first floor', () => {
		expect(floorAt(config, null)?.id).toBe('first');
		expect(floorAt(config, 99)?.id).toBe('first');
		expect(floorAt(null, 1)).toBeNull();
	});
});

describe('advance', () => {
	it('moves at the speed and stops at the end', () => {
		expect(advance(T0, 100, 10, T0 + 60_000)).toEqual({ cursor: T0 + 1000, atEnd: false });
		expect(advance(T0 + 59_500, 100, 10, T0 + 60_000)).toEqual({ cursor: T0 + 60_000, atEnd: true });
	});
});

describe('viewAt', () => {
	const fixes = [fix(0, 2, 2), fix(10, 7, 2), fix(20, 2, 2, 4)];

	it("gives the fix, its floor and room, and a trail with each point's room", () => {
		const view = viewAt(config, fixes, T0 + 10_000, 60_000, false);
		expect(view.current?.x).toBe(7);
		expect(view.floor?.id).toBe('first');
		expect(view.room?.id).toBe('den');
		expect(view.trail.map((p) => p.room?.id)).toEqual(['kitchen', 'den']);
		expect(view.trail.map((p) => p.floor?.id)).toEqual(['first', 'first']);
		expect(view.live).toBe(false);
	});

	it("follows a fix to another floor, and the trail keeps each point's own floor", () => {
		const view = viewAt(config, fixes, T0 + 20_000, 60_000, true);
		expect(view.floor?.id).toBe('second');
		expect(view.room?.id).toBe('office');
		expect(view.trail.map((p) => p.floor?.id)).toEqual(['first', 'first', 'second']);
	});

	it('has no fix, floor or room when the device was quiet', () => {
		const view = viewAt(config, fixes, T0 + 20 * 60_000, 60_000, false);
		expect(view.current).toBeNull();
		expect(view.floor).toBeNull();
		expect(view.trail).toEqual([]);
	});
});

describe('isOtherDay', () => {
	it('tells a stamp from another calendar day', () => {
		expect(isOtherDay(T0, T0 + 1000)).toBe(false);
		expect(isOtherDay(T0, T0 + 48 * 3_600_000)).toBe(true);
	});
});
