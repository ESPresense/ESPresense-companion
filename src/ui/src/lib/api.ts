import { base } from '$app/paths';

/**
 * Builds a backend URL that respects the app's base path (e.g. Home Assistant ingress).
 * Use this for `/api/...` and `/ws...` paths instead of `resolve()`, which is typed for SvelteKit routes only.
 */
export function apiUrl(path: string): string {
	return `${base}${path}`;
}

/**
 * Thrown by {@link apiFetch} when the backend responds with a non-2xx status.
 * `message` is the `error`/`message` field of a JSON error body when present,
 * otherwise the response status text (or `HTTP <status>`).
 */
export class ApiError extends Error {
	readonly status: number;
	readonly statusText: string;
	readonly body: unknown;

	constructor(status: number, statusText: string, message: string, body?: unknown) {
		super(message);
		this.name = 'ApiError';
		this.status = status;
		this.statusText = statusText;
		this.body = body;
	}

	static async fromResponse(response: Response): Promise<ApiError> {
		let body: unknown = undefined;
		let message = response.statusText || `HTTP ${response.status}`;
		try {
			const text = await response.text();
			if (text) {
				try {
					body = JSON.parse(text);
					const json = body as { error?: unknown; message?: unknown };
					if (typeof json?.error === 'string') message = json.error;
					else if (typeof json?.message === 'string') message = json.message;
				} catch {
					body = text;
				}
			}
		} catch {
			// body unreadable; keep the status-based message
		}
		return new ApiError(response.status, response.statusText, message, body);
	}
}

/**
 * `fetch(apiUrl(path), init)` that throws {@link ApiError} on a non-2xx response and
 * returns the parsed JSON body. A 204 / empty body resolves to `undefined`.
 */
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
	const response = await fetch(apiUrl(path), init);
	if (!response.ok) throw await ApiError.fromResponse(response);
	if (response.status === 204) return undefined as T;
	const text = await response.text();
	return (text ? JSON.parse(text) : undefined) as T;
}
