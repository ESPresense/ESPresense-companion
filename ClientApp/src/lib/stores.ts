import { readable } from 'svelte/store';
import { writable } from 'svelte/store';


export interface Room {
	name: string;
	points: number[][];
}

export interface Floor {
	name: string;
	z: number;
	rooms: Room[];
}

export interface Node {
	name: string;
	id?: any;
	point: number[];
}

export interface Device {
	name: string;
	id: string;
}

export interface Config {
	bounds: number[][];
	timeout: number;
	awayTimeout: number;
	floors: Floor[];
	nodes: Node[];
	devices: Device[];
}

export const config = writable<Config>();
export const nodes = writable();

let socket: WebSocket;

async function getConfig(){
	const response = await fetch(`/api/state/config`);
	config.set(await response.json());

	socket = new WebSocket(`${location.origin.replace('http://','ws://')}/ws`);

	socket.addEventListener('open', function (event) {
		console.log("It's open");
	});

	socket.addEventListener('message', async function (event) {
		var eventData = JSON.parse(event.data);
		config.set(await response.json());
	});
}
getConfig();


async function getNodes() {
	const response = await fetch(`/api/state/nodes`);
	nodes.set(await response.json());
}
getNodes();

export const devices = readable([], function start(set) {
	var errors = 0;
	var outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		fetch(`/api/state/devices`)
			.then(d => d.json())
			.then(r => {
				outstanding = false;
				errors = 0;
				set(r);
			})
			.catch((ex) => {
				outstanding = false;
				if (errors > 5) set(null);
				console.log(ex);
			});
	}, 1000)

	return function stop() {
		clearInterval(interval);
	};
});

