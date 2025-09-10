// Shared color helpers for rooms and devices
// - Honors explicit room.color from config when present
// - Deterministically assigns colors for unspecified rooms using a stable palette

import { schemeCategory10 } from 'd3';
import type { Config } from './types';

const palette: string[] = schemeCategory10 as unknown as string[];

/**
 * Deterministically maps a string to a non-negative 32-bit integer hash.
 *
 * The result is produced by iterating over the string's UTF-16 code units and
 * applying a simple bitwise rolling hash; the final 32-bit signed integer is
 * converted to its absolute value before returning. This is suitable for
 * stable indexing (e.g., selecting a color from a palette) but is not
 * cryptographically secure and can produce collisions.
 *
 * @param str - Input string to hash
 * @returns A non-negative 32-bit integer derived from `str`
 */
function hashStringToInt(str: string): number {
	let hash = 0;
	for (let i = 0; i < str.length; i++) {
		hash = (hash << 5) - hash + str.charCodeAt(i);
		hash |= 0; // Convert to 32bit int
	}
	return Math.abs(hash);
}

/**
 * Normalize a hex color string to an uppercase 7-character `#RRGGBB` form.
 *
 * Accepts short (`#RGB`) or full (`#RRGGBB`) hex formats and returns the expanded
 * uppercase `#RRGGBB` string. Returns `undefined` for missing values or unsupported formats.
 *
 * @param color - The input hex color (may be `#RGB`, `#RRGGBB`, or null/undefined)
 * @returns The normalized `#RRGGBB` string, or `undefined` if the input is invalid or unsupported
 */
function normalizeHex(color: string | undefined | null): string | undefined {
	if (!color) return undefined;
	const c = color.trim();
	// Accept #RGB or #RRGGBB; normalize to #RRGGBB uppercase
	if (/^#([0-9a-fA-F]{3})$/.test(c)) {
		const r = c[1];
		const g = c[2];
		const b = c[3];
		return `#${r}${r}${g}${g}${b}${b}`.toUpperCase();
	}
	if (/^#([0-9a-fA-F]{6})$/.test(c)) {
		return c.toUpperCase();
	}
	// Unsupported format, ignore
	return undefined;
}

/**
 * Returns the color for a room: an explicit, normalized hex color from config if present and valid, otherwise a deterministic fallback from the shared palette.
 *
 * If `config` or `roomId` is missing, or the room has no valid color, a palette color is chosen deterministically by hashing `roomId` so the same `roomId` always maps to the same fallback.
 *
 * The returned value is a hex color string in `#RRGGBB` form (uppercase). Invalid or unsupported explicit color formats in the config are ignored in favor of the fallback.
 *
 * @param config - Optional configuration containing floors and rooms; searched for a room with matching `roomId`.
 * @param roomId - The room identifier used to look up an explicit color and to seed the deterministic fallback.
 * @returns A hex color string (`#RRGGBB`) for the given room.
 */
export function getRoomColor(config: Config | null | undefined, roomId: string | null | undefined): string {
	const fallback = palette[hashStringToInt(roomId ?? '') % palette.length];
	if (!config || !roomId) return fallback;

	for (const floor of config.floors ?? []) {
		for (const room of floor.rooms ?? []) {
			if (room.id === roomId) {
				const explicit = normalizeHex((room as any).color);
				return explicit ?? fallback;
			}
		}
	}

	return fallback;
}

/**
 * Converts a hex color string (e.g., `#RRGGBB` or `#RRGGBBAA`) to a 24-bit RGB number.
 *
 * Accepts strings starting with `#` and length 7 or 9. If input is invalid, returns white (`0xffffff`).
 *
 * @param color - Hex color string to convert.
 * @returns The integer RGB value parsed from the first six hex digits (0xRRGGBB), or `0xffffff` for invalid input.
 */
export function hexToThreeNumber(color: string): number {
	if (!color || color[0] !== '#' || (color.length !== 7 && color.length !== 9)) {
		return 0xffffff;
	}
	return parseInt(color.slice(1, 7), 16);
}
