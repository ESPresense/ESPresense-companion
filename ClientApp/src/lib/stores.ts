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

async function getData(){
	const response = await fetch(`/api/state/config`);
	config.set(await response.json());
}
getData();


export const nodes = readable([], function start(set) {
	var errors = 0;
	var outstanding = false;
	const interval = setInterval(() => {
		if (outstanding) return;
		outstanding = true;
		fetch(`/api/state/nodes`)
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
	}, 60000)

	return function stop() {
		clearInterval(interval);
	};
});

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

