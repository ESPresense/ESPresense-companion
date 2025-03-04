import { readable, writable, derived, get } from 'svelte/store';
import { base } from '$app/paths';
import type { Device, Config, Node, CalibrationResponse, DeviceSetting } from './types';
import { WSManager } from './wsManager';

export const showAll = writable<boolean>(false);
export const config = writable<Config>();

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

// Instead of using a query parameter for filtering "untracked", we send a WS filter command.
export const showUntracked = writable<boolean>(false);

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

// Devices store: initial polling load then WS updates
export const devices = readable<Device[]>([], function start(set) {
	let deviceMap = new Map<string, Device>();

	// Update our store based on the device map
	function updateDevicesFromMap() {
		set(Array.from(deviceMap.values()));
	}

	// Initial polling load (note: no filter in URL)
	function fetchDevices() {
		fetch(`${base}/api/state/devices`)
			.then((d) => d.json())
			.then((r: Device[]) => {
				deviceMap = new Map(r.map((device: Device) => [device.id, device]));
				updateDevicesFromMap();
			})
			.catch((ex) => {
				console.error('Error fetching devices:', ex);
			});
	}
	fetchDevices();
	const pollingInterval = setInterval(fetchDevices, 60000);

	// WS event subscriptions
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

	// When showUntracked changes, send a WS message to update the filter
	const unsubscribeShowUntracked = showUntracked.subscribe((value) => {
		wsManager.sendMessage({
			command: 'changeFilter',
			type: 'untracked',
			value: value
		});
	});

	return () => {
		clearInterval(pollingInterval);
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
