import { base } from '$app/paths';

/**
 * Builds a backend URL that respects the app's base path (e.g. Home Assistant ingress).
 * Use this for `/api/...` calls instead of `resolve()`, which is typed for SvelteKit routes only.
 */
export function apiUrl(path: string): string {
	return `${base}${path}`;
}
