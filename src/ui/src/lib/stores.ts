import { readable, writable, derived, get } from 'svelte/store';
import { apiFetch } from '$lib/api';
import type { Device, Config, Node, CalibrationResponse, DeviceSetting } from './types';
import { WSManager } from './wsManager';

export const config = writable<Config>();
export const showAll = writable<boolean>(false);
export const showAllFloors = writable<boolean>(false);

export const relativeTimer = function () {
	let interval: NodeJS.Timeout | undefined;
	let startTime = Date.now();
	const { subscribe, set: setStore } = writable(0);

	function start() {
		// Drives "time since last hit" animations; 100ms is plenty and avoids
		// re-rendering every marker several hundred times a second.
		interval = setInterval(() => {
			setStore(Date.now() - startTime);
		}, 100);
	}

	function set(basis: number) {
		startTime = Date.now() - basis;
		setStore(Date.now() - startTime);
	}

	start();

	return {
		subscribe,
		set,
		stop: () => clearInterval(interval)
	};
};

export const relative = relativeTimer();

// Create a singleton WSManager instance
export const wsManager = new WSManager();

// Device message events and subscriptions
export const history = writable<string[]>(['/']);

/**
 * Fetches configuration from the backend and updates the `config` store.
 *
 * Retrieves JSON from `/api/state/config` and sets the exported writable `config` store with the response.
 * Called fire-and-forget (at module load and on every `configChanged` event), so failures are
 * logged here and the store keeps its previous value.
 */
async function getConfig() {
	try {
		config.set(await apiFetch<Config>('/api/state/config'));
	} catch (error) {
		console.error('Error fetching config:', error);
	}
}
getConfig();

export const deviceSettings = writable<DeviceSetting[] | null>([], function start(set) {
	let settings: DeviceSetting[] = [];
	let outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		apiFetch<DeviceSetting[]>('/api/devices')
			.then((r) => {
				outstanding = false;
				settings = r;
				set(settings);
			})
			.catch((ex) => {
				outstanding = false;
				console.error(ex);
			});
	}, 60000);

	return () => {
		clearInterval(interval);
	};
});

export const devices = readable<Device[]>([], function start(set) {
	let deviceMap = new Map<string, Device>();
	let pollTimer: NodeJS.Timeout;
	let isPolling = false;

	function updateDevicesFromMap() {
		set(Array.from(deviceMap.values()));
	}

	async function fetchDevices() {
		if (isPolling) return;

		isPolling = true;
		try {
			const devices = await apiFetch<Device[]>(`/api/state/devices?showAll=${get(showAll)}`);

			// Replace the entire map instead of accumulating devices
			deviceMap = new Map(devices.map((device: Device) => [device.id, device]));
			updateDevicesFromMap();
		} catch (error) {
			console.error('Error fetching devices:', error);
		} finally {
			isPolling = false;
		}
	}

	fetchDevices();
	pollTimer = setInterval(fetchDevices, 60000);

	const deviceChangedCallback = (data: Device) => {
		if (data?.id) {
			deviceMap.set(data.id, data);
			updateDevicesFromMap();
		}
	};
	wsManager.subscribeToEvent('deviceChanged', deviceChangedCallback);

	const deviceRemovedCallback = (payload: any) => {
		const id = payload?.deviceId || payload?.id || payload;
		if (id && deviceMap.has(id)) {
			deviceMap.delete(id);
			updateDevicesFromMap();
		}
	};
	wsManager.subscribeToEvent('deviceRemoved', deviceRemovedCallback);

	const configChangedCallback = (data: Config) => {
		getConfig();
	};
	wsManager.subscribeToEvent('configChanged', configChangedCallback);

	const timeCallback = (data: number) => {
		relative.set(data);
	};
	wsManager.subscribeToEvent('time', timeCallback);

	function sendFilter() {
		wsManager.sendMessage({
			command: 'changeFilter',
			type: 'showAll',
			value: '' + get(showAll)
		});
	}

	// Subscribe to showAll changes and trigger immediate poll when it changes
	const unsubscribeShowUntracked = showAll.subscribe(() => {
		sendFilter();
		// Force an immediate poll when showAll changes
		fetchDevices();
	});

	// The server-side filter is per-connection, so it must be re-sent after a reconnect
	const unsubscribeOpen = wsManager.onOpen(sendFilter);

	return () => {
		clearInterval(pollTimer);
		wsManager.unsubscribeFromEvent('deviceChanged', deviceChangedCallback);
		wsManager.unsubscribeFromEvent('deviceRemoved', deviceRemovedCallback);
		wsManager.unsubscribeFromEvent('configChanged', configChangedCallback);
		wsManager.unsubscribeFromEvent('time', timeCallback);
		unsubscribeShowUntracked();
		unsubscribeOpen();
	};
});

// Nodes store (polling)
export const nodes = readable<Node[]>([], function start(set) {
	let errors = 0;
	let outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		apiFetch<Node[]>('/api/state/nodes?includeTele=true')
			.then((r) => {
				outstanding = false;
				errors = 0;
				set(r);
			})
			.catch((ex) => {
				outstanding = false;
				errors++;
				if (errors > 5) set([]);
				console.error(ex);
			});
	}, 1000);

	return () => {
		clearInterval(interval);
	};
});

function createCalibrationStore() {
	let activeRefresh: (() => Promise<void>) | undefined;

	const store = readable<CalibrationResponse>({ matrix: {} }, function start(set) {
		let request: AbortController | undefined;
		let stopped = false;

		async function fetchAndSet() {
			if (stopped || request || (typeof document !== 'undefined' && document.visibilityState === 'hidden')) return;

			const controller = new AbortController();
			request = controller;

			try {
				// Timeout so a stalled request can't wedge `request` and stop all later polls.
				const data = await apiFetch<CalibrationResponse>('/api/state/calibration', {
					cache: 'no-store',
					signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10000)])
				});
				if (!stopped) set(data);
			} catch (error) {
				if ((error as Error)?.name !== 'AbortError') {
					console.error('Error fetching calibration:', error);
				}
			} finally {
				if (request === controller) request = undefined;
			}
		}

		const refreshWhenVisible = () => {
			if (document.visibilityState === 'visible') void fetchAndSet();
		};

		activeRefresh = fetchAndSet;
		void fetchAndSet();
		const interval = setInterval(fetchAndSet, 5000);
		if (typeof document !== 'undefined') document.addEventListener('visibilitychange', refreshWhenVisible);
		if (typeof window !== 'undefined') window.addEventListener('focus', fetchAndSet);

		return () => {
			stopped = true;
			request?.abort();
			clearInterval(interval);
			if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', refreshWhenVisible);
			if (typeof window !== 'undefined') window.removeEventListener('focus', fetchAndSet);
			if (activeRefresh === fetchAndSet) activeRefresh = undefined;
		};
	});

	return {
		subscribe: store.subscribe,
		refresh: () => activeRefresh?.() ?? Promise.resolve()
	};
}

// Calibration changes continuously while optimization collects and validates samples.
export const calibration = createCalibrationStore();
