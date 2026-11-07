import { polygonContains } from 'd3';
import type { Config, DeviceHistory, Floor, Room } from './types';

/** One stored fix of the best scenario, `when` parsed to epoch milliseconds. */
export interface HistoryFix {
	t: number;
	x: number;
	y: number;
	z: number | null;
	confidence: number;
	scenario: string | null;
}

export interface TrailPoint {
	fix: HistoryFix;
	/** 0 at the moment shown, 1 at the far end of the trail window. */
	age: number;
	/** The room the fix fell in, on the floor its height puts it on. */
	room: Room | null;
}

/** A device's history at one moment: what the map draws and what the page shows. */
export interface HistoryView {
	/** Following the present: the live marker shows, with the trail behind it. */
	live: boolean;
	/** The moment shown, epoch ms. */
	cursor: number;
	/** The stored fix in force at the cursor; null when the device had none then. */
	current: HistoryFix | null;
	/** Oldest first, ending at the cursor. */
	trail: TrailPoint[];
	floor: Floor | null;
	room: Room | null;
}

const HOUR = 3_600_000;

/** A .NET TimeSpan string ("1.00:00:00", "12:30:00", "00:00:05.5") in milliseconds; null when it isn't one. */
export function parseTimeSpan(s: string | null | undefined): number | null {
	if (!s) return null;
	const m = /^(-)?(?:(\d+)\.)?(\d+):(\d\d):(\d\d)(?:\.(\d+))?$/.exec(s.trim());
	if (!m) return null;
	const [, sign, days, hours, minutes, seconds, fraction] = m;
	const ms = (Number(days ?? 0) * 24 + Number(hours)) * HOUR + Number(minutes) * 60_000 + Number(seconds) * 1000 + (fraction ? Math.round(Number(`0.${fraction}`) * 1000) : 0);
	return sign ? -ms : ms;
}

/** How long history is kept (history.expire_after); 24 h when the config doesn't say. */
export function retentionMs(config: Config | null | undefined): number {
	return parseTimeSpan(config?.history?.expireAfterTimeSpan) ?? 24 * HOUR;
}

/** Bucket size in seconds that loads a window as at most `maxPoints` fixes (the API's `every` parameter). */
export function sampleEverySeconds(windowMs: number, maxPoints = 10_000): number {
	return Math.max(1, Math.ceil(windowMs / 1000 / maxPoints));
}

/**
 * The rows that count as the device's position, as fixes, oldest first. Only the best scenario's rows do;
 * databases written before `best` was recorded have none marked, so then every row does.
 */
export function toFixes(rows: DeviceHistory[]): HistoryFix[] {
	const best = rows.some((r) => r.best) ? rows.filter((r) => r.best) : rows;
	return best
		.filter((r) => r.x != null && r.y != null && r.when)
		.map((r) => ({ t: Date.parse(r.when), x: r.x as number, y: r.y as number, z: r.z, confidence: r.confidence, scenario: r.scenario }))
		.filter((f) => Number.isFinite(f.t))
		.sort((a, b) => a.t - b.t);
}

/** `existing` and `incoming` together, one fix per instant, dropping everything before `since`. */
export function mergeFixes(existing: HistoryFix[], incoming: HistoryFix[], since: number): HistoryFix[] {
	const byTime = new Map<number, HistoryFix>();
	for (const f of existing) if (f.t >= since) byTime.set(f.t, f);
	for (const f of incoming) if (f.t >= since) byTime.set(f.t, f);
	return [...byTime.values()].sort((a, b) => a.t - b.t);
}

/** Index of the newest fix at or before t, or -1 when t is before the first. */
function indexAt(fixes: HistoryFix[], t: number): number {
	let lo = 0;
	let hi = fixes.length - 1;
	let found = -1;
	while (lo <= hi) {
		const mid = (lo + hi) >> 1;
		if (fixes[mid].t <= t) {
			found = mid;
			lo = mid + 1;
		} else hi = mid - 1;
	}
	return found;
}

/** The fix in force at t: the newest at or before it, unless that one is older than maxAgeMs, when the device had gone quiet. */
export function fixAt(fixes: HistoryFix[], t: number, maxAgeMs = 5 * 60_000): HistoryFix | null {
	const i = indexAt(fixes, t);
	if (i < 0) return null;
	return t - fixes[i].t > maxAgeMs ? null : fixes[i];
}

/** The fixes in the windowMs up to t, oldest first, each aged 0 (at t) to 1 (at the window's start). */
export function trailBefore(fixes: HistoryFix[], t: number, windowMs: number): { fix: HistoryFix; age: number }[] {
	if (windowMs <= 0) return [];
	const out: { fix: HistoryFix; age: number }[] = [];
	for (let i = indexAt(fixes, t); i >= 0 && t - fixes[i].t <= windowMs; i--) out.push({ fix: fixes[i], age: (t - fixes[i].t) / windowMs });
	return out.reverse();
}

/** The floor whose height range holds z, as the backend places a fix; the first floor when z is unknown or on no floor. */
export function floorAt(config: Config | null | undefined, z: number | null | undefined): Floor | null {
	const floors = config?.floors ?? [];
	if (z != null) {
		const hit = floors.find((f) => f.bounds?.length >= 2 && z >= f.bounds[0][2] && z <= f.bounds[1][2]);
		if (hit) return hit;
	}
	return floors[0] ?? null;
}

/** The room on `floor` containing (x, y); null outside every room. */
export function roomAt(floor: Floor | null | undefined, x: number, y: number): Room | null {
	return floor?.rooms?.find((r) => polygonContains(r.points, [x, y])) ?? null;
}

/** Where a playing cursor lands after dtMs of wall time at `speed`; it stops at `end`. */
export function advance(cursor: number, dtMs: number, speed: number, end: number): { cursor: number; atEnd: boolean } {
	const next = cursor + dtMs * speed;
	return next >= end ? { cursor: end, atEnd: true } : { cursor: next, atEnd: false };
}

/** The history at one moment: the fix then, its floor and room, and the trail of the trailMs before it. */
export function viewAt(config: Config | null | undefined, fixes: HistoryFix[], cursor: number, trailMs: number, live: boolean): HistoryView {
	const current = fixAt(fixes, cursor);
	const floor = current ? floorAt(config, current.z) : null;
	const trail = trailBefore(fixes, cursor, trailMs).map((p) => ({ ...p, room: roomAt(floorAt(config, p.fix.z), p.fix.x, p.fix.y) }));
	return { live, cursor, current, trail, floor, room: current && floor ? roomAt(floor, current.x, current.y) : null };
}

/** HH:MM:SS in the browser's time zone. */
export function formatClock(t: number): string {
	return new Date(t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

/** True when t is on another calendar day than `now` in the browser's time zone. */
export function isOtherDay(t: number, now: number): boolean {
	return new Date(t).toDateString() !== new Date(now).toDateString();
}
