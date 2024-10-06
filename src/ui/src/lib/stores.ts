import { readable, writable, derived } from 'svelte/store';
import { base } from '$app/paths';
import type { Device, Config, Node, CalibrationResponse } from './types';

export const showAll: SvelteStore<boolean> = writable(false);
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

let socket: WebSocket;

export const history = writable(['/']);

async function getConfig() {
	const response = await fetch(`${base}/api/state/config`);
	config.set(await response.json());
}

getConfig();

export const showUntracked = writable<boolean>(false);

export const devices = derived<[typeof showUntracked], Device[]>(
	[showUntracked],
	([$showUntracked], set) => {
		let deviceMap = new Map();
		var q = (new URLSearchParams({
			showUntracked: $showUntracked ? "true" : "false"
		})).toString();

		function updateDevicesFromMap() {
			const devicesArray = Array.from(deviceMap.values());
			set(devicesArray);
		}

		function fetchDevices() {

			fetch(`${base}/api/state/devices?${q}`)
				.then((d) => d.json())
				.then((r) => {
					deviceMap = new Map(r.map((device: Device) => [device.id, device]));
					updateDevicesFromMap();
				})
				.catch((ex) => {
					console.error('Error fetching devices:', ex);
				});
		}

		fetchDevices();

		const interval = setInterval(fetchDevices, 60000);

		function setupWebsocket() {
			const loc = new URL(`${base}/ws?${q}`, window.location.href);
			const new_uri = (loc.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + loc.host + loc.pathname + loc.search;
			const socket = new WebSocket(new_uri);

			socket.addEventListener('message', async function (event) {
				const eventData = JSON.parse(event.data);
				if (eventData.type === 'deviceChanged' && eventData.data?.id) {
					deviceMap.set(eventData.data.id, eventData.data);
					updateDevicesFromMap();
				} else if (eventData.type === 'configChanged') {
					getConfig();
				} else if (eventData.type === 'time') {
					relative.set(eventData.data);
				} else {
					console.log('Unhandled websocket event:', event.data);
				}
			});

			return socket;
		}

		const socket = setupWebsocket();

		return () => {
			clearInterval(interval);
			socket.close();
		};
	}
);

export const nodes = readable<Node[]>([], function start(set) {
	var errors = 0;
	var outstanding = false;
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
				if (errors > 5) set([]);
				console.log(ex);
			});
	}, 1000);

	return function stop() {
		clearInterval(interval);
	};
});

export const calibration = readable<CalibrationResponse>({matrix: {}}, function start(set) {
	async function fetchAndSet() {
		const response = await fetch(`${base}/api/state/calibration`);
		var data = await response.json();
		set(data);
	}

	fetchAndSet();
	const interval = setInterval(() => {
		fetchAndSet();
	}, 1000);

	return function stop() {
		clearInterval(interval);
	};
});
