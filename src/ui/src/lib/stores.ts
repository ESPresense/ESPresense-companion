import { readable, writable, derived, get } from 'svelte/store';
import { base } from '$app/paths';
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
		interval = setInterval(() => {
			setStore(Date.now() - startTime);
		}, 1);
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

// Load config initially
async function getConfig() {
	const response = await fetch(`${base}/api/state/config`);
	config.set(await response.json());
}
getConfig();


export const deviceSettings = writable<DeviceSetting[] | null>([], function start(set) {
	let settings: DeviceSetting[] = [];
	let outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		fetch(`${base}/api/devices`)
			.then((d) => d.json())
			.then((r: DeviceSetting[]) => {
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
			const response = await fetch(`${base}/api/state/devices?showAll=${get(showAll)}`);
			if (!response.ok) throw new Error(`HTTP error! Status: ${response.status}`);

			const devices: Device[] = await response.json();

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

	const configChangedCallback = (data: Config) => {
		getConfig();
	};
	wsManager.subscribeToEvent('configChanged', configChangedCallback);

	const timeCallback = (data: number) => {
		relative.set(data);
	};
	wsManager.subscribeToEvent('time', timeCallback);

	// Subscribe to showAll changes and trigger immediate poll when it changes
	const unsubscribeShowUntracked = showAll.subscribe((value) => {
		wsManager.sendMessage({
			command: 'changeFilter',
			type: 'showAll',
			value: ''+value
		});

		// Force an immediate poll when showAll changes
		fetchDevices();
	});

	return () => {
		clearInterval(pollTimer);
		wsManager.unsubscribeFromEvent('deviceChanged', deviceChangedCallback);
		wsManager.unsubscribeFromEvent('configChanged', configChangedCallback);
		wsManager.unsubscribeFromEvent('time', timeCallback);
		unsubscribeShowUntracked();
	};
});

// Nodes store (polling)
export const nodes = readable<Node[]>([], function start(set) {
	let errors = 0;
	let outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		fetch(`${base}/api/state/nodes?includeTele=true`)
			.then((d) => d.json())
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

// Calibration polling store
export const calibration = readable<CalibrationResponse>({ matrix: {} }, function start(set) {
	async function fetchAndSet() {
		const response = await fetch(`${base}/api/state/calibration`);
		const data = await response.json();
		set(data);
	}
	fetchAndSet();
	const interval = setInterval(fetchAndSet, 1000);
	return () => clearInterval(interval);
});