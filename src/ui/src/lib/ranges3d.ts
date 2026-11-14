// Range spheres in the 3D view: where a node's measured distance to a device meets the floor.
/**
 * Radius of the circle a range sphere cuts in a horizontal plane: the sphere has radius `distance` around
 * a node `height` above (or below) that plane. Null when the sphere does not reach the plane.
 */
export function floorRingRadius(distance: number, height: number): number | null {
	if (!(distance > 0)) return null;
	const squared = distance * distance - height * height;
	return squared > 0 ? Math.sqrt(squared) : null;
}

/**
 * A colour per node, evenly spaced round the hue wheel so no two nodes share one however many there are.
 * Steady for as long as the set of nodes is: a node's hue goes by its place among the sorted ids.
 */
export function nodeRangeColors(nodeIds: string[]): Map<string, string> {
	const sorted = [...new Set(nodeIds)].sort();
	return new Map(sorted.map((id, i) => [id, `hsl(${Math.round((i * 360) / sorted.length)}, 80%, 62%)`]));
}
