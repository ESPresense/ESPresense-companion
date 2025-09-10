import type { ConfigGps } from './types'; // Assuming ConfigGps is defined in types.ts

const R = 6378137; // Earth's mean radius in meters

/**
 * Convert local internal X/Y coordinates into GPS latitude and longitude using a GPS origin and rotation.
 *
 * The function treats `x` as the East/West axis and `y` as the North/South axis before applying the
 * configured rotation (in degrees) from `gpsConfig`. Rotation defaults to 0° when not provided.
 *
 * @param x - Internal X coordinate (east positive) relative to the GPS origin.
 * @param y - Internal Y coordinate (north positive) relative to the GPS origin.
 * @param gpsConfig - GPS origin and options; must include `latitude` and `longitude`, and may include `rotation` (degrees).
 * @returns An object { latitude, longitude } on success, or `null` if inputs are missing, the longitude cannot be computed
 *          near the poles, or the resulting coordinates are NaN/out of valid ranges.
 */
export function internalToGps(x: number | null | undefined, y: number | null | undefined, gpsConfig: ConfigGps | null | undefined): { latitude: number; longitude: number } | null {
	// Check for null or undefined inputs
	if (x == null || y == null || gpsConfig?.latitude == null || gpsConfig?.longitude == null) {
		return null;
	}

	const lat = gpsConfig.latitude;
	const lon = gpsConfig.longitude;
	// Default rotation to 0 if null or undefined
	const rotationDeg = gpsConfig.rotation ?? 0.0;

	// Convert rotation from degrees to radians
	const rotationRad = (rotationDeg * Math.PI) / 180.0;

	// Rotate the coordinates (counter-clockwise rotation of point for clockwise system rotation)
	// x' = x * cos(theta) + y * sin(theta)
	// y' = -x * sin(theta) + y * cos(theta)
	const cosTheta = Math.cos(rotationRad);
	const sinTheta = Math.sin(rotationRad);
	const xRotated = x * cosTheta + y * sinTheta;
	const yRotated = -x * sinTheta + y * cosTheta;

	// Calculate latitude change (North/South) based on rotated Y
	const dLat = yRotated / R;

	// Calculate longitude change (East/West) based on rotated X, adjusted for latitude
	// Ensure latitude is not near the poles for the cosine calculation
	const cosLat = Math.cos((Math.PI * lat) / 180.0);
	if (Math.abs(cosLat) < 1e-9) {
		// Avoid division by zero near poles
		console.warn('Cannot calculate longitude change near poles.');
		return null;
	}
	const dLon = xRotated / (R * cosLat);

	// Calculate the new latitude and longitude
	const newLat = lat + (dLat * 180.0) / Math.PI;
	const newLon = lon + (dLon * 180.0) / Math.PI;

	// Basic validation for calculated coordinates
	if (isNaN(newLat) || isNaN(newLon) || newLat < -90 || newLat > 90 || newLon < -180 || newLon > 180) {
		console.warn('Calculated invalid GPS coordinates:', { newLat, newLon });
		return null;
	}

	return { latitude: newLat, longitude: newLon };
}
