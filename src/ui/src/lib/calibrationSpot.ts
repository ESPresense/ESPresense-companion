export type CalibrationSpot = { x: number; y: number; z?: number };

/**
 * The saved position of an anchored device, for opening the calibration page.
 * Null unless x, y and z are all set, which is what makes a device anchored.
 * Height is relative to the floor and may be negative for a device below it.
 */
export function anchorSpot(
	settings: { x?: number | null; y?: number | null; z?: number | null } | null | undefined,
	floorLowerZ: number
): { spot: CalibrationSpot; height: number } | null {
	const { x, y, z } = settings ?? {};
	if (x == null || y == null || z == null) return null;
	return { spot: { x, y, z }, height: z - floorLowerZ };
}
