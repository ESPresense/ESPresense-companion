import { readable, writable, derived, type Writable } from 'svelte/store';
import { apiFetch, apiUrl } from '$lib/api';
import type { FirmwareManifest, Release, WorkflowRun } from '$lib/types';

export const updateMethod: Writable<string> = writable('self');
export const firmwareSource: Writable<string> = writable('release');
// Initial values match VersionPicker's prop fallbacks; Svelte 5 refuses to bind
// undefined to a prop that declares a fallback (props_invalid_value).
export const flavor: Writable<string> = writable('-');
export const version: Writable<string> = writable('');
export const artifact: Writable<string> = writable('');

export const firmwareTypes = writable<FirmwareManifest | null>(null, function start(set) {
	// One-shot manifest fetch with a bounded retry (same spirit as artifacts/releases below):
	// otherwise a single non-2xx would leave the store null forever with no visible error.
	let errors = 0;
	let retryTimer: ReturnType<typeof setTimeout> | undefined;

	function fetchData() {
		apiFetch<FirmwareManifest>('/api/firmware/types')
			.then((r) => set(r))
			.catch((ex) => {
				console.error('Error fetching firmware types:', ex);
				if (++errors <= 5) retryTimer = setTimeout(fetchData, 15000);
			});
	}

	fetchData();

	return function stop() {
		clearTimeout(retryTimer);
	};
});

export const cpuNames = derived(firmwareTypes, (a) =>
	a?.cpus?.reduce((acc, cur) => {
		acc.set(cur.value, cur.name);
		return acc;
	}, new Map<string, string>())
);

export const flavorNames = derived(firmwareTypes, (a) =>
	a?.flavors?.reduce((acc, cur) => {
		acc.set(cur.value, cur.name);
		return acc;
	}, new Map<string, string>())
);

export type GitHubData<T> = { data: Map<string, T[]> | null; error: string | null };

/**
 * Polls an unauthenticated GitHub API endpoint (60 req/hr per IP, shared by every tab and by
 * releases + artifacts). Non-2xx responses surface as `error` instead of leaving the UI spinning,
 * and a rate-limit response waits until GitHub's reset time before retrying.
 */
function githubStore<T>(url: string, intervalMs: number, transform: (json: any) => Map<string, T[]>) {
	return readable<GitHubData<T>>({ data: null, error: null }, function start(set) {
		let data: Map<string, T[]> | null = null;
		let errors = 0;
		let timer: ReturnType<typeof setTimeout> | undefined;
		let stopped = false;

		async function fetchData() {
			let delay = intervalMs;
			try {
				const res = await fetch(url);
				if (!res.ok) {
					const remaining = res.headers.get('x-ratelimit-remaining');
					const reset = Number(res.headers.get('x-ratelimit-reset'));
					if ((res.status === 403 || res.status === 429) && remaining === '0' && reset) {
						const resetAt = new Date(reset * 1000);
						delay = Math.max(resetAt.getTime() - Date.now(), 0) + 5000;
						throw new Error(`GitHub API rate limit exceeded; retrying at ${resetAt.toLocaleTimeString()}`);
					}
					const body = await res.json().catch(() => null);
					throw new Error(`GitHub API error ${res.status}${body?.message ? `: ${body.message}` : ''}`);
				}
				data = transform(await res.json());
				errors = 0;
				set({ data, error: null });
			} catch (ex) {
				console.error(`Error fetching ${url}:`, ex);
				// Back off quickly-then-slowly for transient failures, but never faster than a rate-limit reset.
				if (delay === intervalMs) delay = Math.min(15000 * 2 ** errors, intervalMs);
				errors++;
				set({ data, error: ex instanceof Error ? ex.message : String(ex) });
			}
			if (!stopped) timer = setTimeout(fetchData, delay);
		}

		fetchData();

		return function stop() {
			stopped = true;
			clearTimeout(timer);
		};
	});
}

function groupBy<T>(items: T[], key: (item: T) => string): Map<string, T[]> {
	return items.reduce((p, c) => {
		const k = key(c);
		const list = p.get(k);
		if (list) list.push(c);
		else p.set(k, [c]);
		return p;
	}, new Map<string, T[]>());
}

export const artifacts = githubStore<WorkflowRun>('https://api.github.com/repos/ESPresense/ESPresense/actions/workflows/build.yml/runs?status=success&per_page=100', 5 * 60000, (json: { workflow_runs: WorkflowRun[] }) =>
	groupBy(
		json.workflow_runs.filter((i) => i.head_repository.full_name === 'ESPresense/ESPresense' && i.status == 'completed' && (i.pull_requests.length > 0 || (i.head_branch == 'main' && Date.now() - +new Date(i.created_at) < 1000 * 60 * 60 * 24 * 7))),
		(i) => i.head_branch
	)
);

// Releases with more than 5 assets, grouped into "Beta" (prerelease) and "Release".
export const releases = githubStore<Release>('https://api.github.com/repos/ESPresense/ESPresense/releases', 15 * 60000, (json: Release[]) =>
	groupBy(
		json.filter((i) => i.assets.length > 5),
		(i) => (i.prerelease ? 'Beta' : 'Release')
	)
);

export function getFirmwareUrl(firmwareSource: string, version: string, artifact: string, firmware: string): string | null {
	if (firmware) {
		switch (firmwareSource) {
			case 'artifact':
				if (artifact) return `https://espresense.com/artifacts/download/runs/${artifact}/${firmware}`;
				break;
			case 'release':
				if (version) return `https://github.com/ESPresense/ESPresense/releases/download/${version}/${firmware}`;
				break;
		}
	}
	return null;
}

export function getLocalFirmwareUrl(firmwareSource: string, version: string, artifact: string, firmware: string): string | null {
	const url = getFirmwareUrl(firmwareSource, version, artifact, firmware);
	if (!url) return null;

	const loc = new URL(apiUrl('/api/firmware/download'), window.location.href);

	const params = new URLSearchParams();
	params.append('url', url);
	loc.search = params.toString();

	return loc.toString();
}

type Callback = (percentComplete: number, message: string) => void;

export async function firmwareUpdate(id: string, url: string, callback: Callback): Promise<void> {
	var loc = new URL(apiUrl(`/ws/firmware/update/${id}`), window.location.href);
	var wsUrl = (loc.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + loc.host + loc.pathname + `?${new URLSearchParams({ url: url })}`;
	const ws = new WebSocket(wsUrl);

	ws.addEventListener('message', (event) => {
		const data = event.data;

		try {
			const json = JSON.parse(data);
			const { percentComplete, message } = json;
			callback(percentComplete, message);
		} catch (e) {
			console.error('Could not parse message:', data);
		}
	});

	ws.addEventListener('error', (event) => {
		console.error(`WebSocket Error: ${event}`);
	});

	ws.addEventListener('close', (event) => {
		if (event.wasClean) {
			console.log(`Connection closed cleanly, code=${event.code}, reason=${event.reason}`);
		} else {
			console.error(`Connection died`);
		}
	});

	return new Promise<void>((resolve, reject) => {
		ws.addEventListener('close', () => {
			resolve();
		});

		ws.addEventListener('error', () => {
			reject(new Error('WebSocket error'));
		});
	});
}
