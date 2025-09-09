// Shared color helpers for rooms and devices
// - Honors explicit room.color from config when present
// - Deterministically assigns colors for unspecified rooms using a stable palette

import { schemeCategory10 } from 'd3';
import type { Config } from './types';

const palette: string[] = schemeCategory10 as unknown as string[];

function hashStringToInt(str: string): number {
	let hash = 0;
	for (let i = 0; i < str.length; i++) {
		hash = (hash << 5) - hash + str.charCodeAt(i);
		hash |= 0; // Convert to 32bit int
	}
	return Math.abs(hash);
}

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

export function hexToThreeNumber(color: string): number {
	if (!color || color[0] !== '#' || (color.length !== 7 && color.length !== 9)) {
		return 0xffffff;
	}
	return parseInt(color.slice(1, 7), 16);
}
