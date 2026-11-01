export type CalibrationSpot = { x: number; y: number; z?: number };

type FloorBounds = { id: string; bounds: number[][] };

/** How far z is from a floor's vertical range: 0 when it's within it */
function distanceFromFloor(z: number, floor: FloorBounds): number {
	return Math.max(floor.bounds[0][2] - z, z - floor.bounds[1][2], 0);
}

/**
 * The saved position of an anchored device, for opening the calibration page.
 * Null unless x, y and z are all set, which is what makes a device anchored, and there are floors to
 * measure from. Uses the floor the device reports if there is one, otherwise the floor nearest its z,
 * so it needs nothing from the device list: a device that's never located never appears there.
 * Height is relative to the floor, in millimetre steps so the box doesn't show 1.7000000000000002, and
 * may be negative for a device below it.
 */
export function anchorSpot(settings: { x?: number | null; y?: number | null; z?: number | null } | null | undefined, floors: FloorBounds[] | null | undefined, reportedFloorId?: string | null): { spot: CalibrationSpot; height: number; floorId: string } | null {
	const { x, y, z } = settings ?? {};
	if (x == null || y == null || z == null || !floors?.length) return null;
	const floor = floors.find((f) => f.id === reportedFloorId) ?? floors.reduce((a, b) => (distanceFromFloor(z, b) < distanceFromFloor(z, a) ? b : a));
	return { spot: { x, y, z }, height: Math.round((z - floor.bounds[0][2]) * 1000) / 1000, floorId: floor.id };
}
