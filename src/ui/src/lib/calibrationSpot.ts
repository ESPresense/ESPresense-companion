export type CalibrationSpot = { x: number; y: number; z?: number };

type FloorBounds = { id: string; bounds: number[][] };

/** How far a point is from a floor's bounds: 0 when it's inside them */
function distanceFromFloor(point: number[], floor: FloorBounds): number {
	const [min, max] = floor.bounds;
	return Math.hypot(...point.map((p, i) => Math.max(min[i] - p, p - max[i], 0)));
}

/**
 * The saved position of an anchored device, for opening the calibration page.
 * Null unless x, y and z are all set, which is what makes a device anchored, and there are floors to
 * measure from. Uses the floor the device reports if there is one, otherwise the first floor containing
 * it, as the backend does, or else the nearest. That needs nothing from the device list: a device
 * that's never located never appears there.
 * Height is relative to the floor, in millimetre steps so the box doesn't show 1.7000000000000002, and
 * may be negative for a device below it.
 */
export function anchorSpot(settings: { x?: number | null; y?: number | null; z?: number | null } | null | undefined, floors: FloorBounds[] | null | undefined, reportedFloorId?: string | null): { spot: CalibrationSpot; height: number; floorId: string } | null {
	const { x, y, z } = settings ?? {};
	if (x == null || y == null || z == null || !floors?.length) return null;
	const floor = floors.find((f) => f.id === reportedFloorId) ?? floors.reduce((a, b) => (distanceFromFloor([x, y, z], b) < distanceFromFloor([x, y, z], a) ? b : a));
	return { spot: { x, y, z }, height: Math.round((z - floor.bounds[0][2]) * 1000) / 1000, floorId: floor.id };
}
