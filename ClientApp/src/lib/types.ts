
export interface Room {
	name: string;
	points: [number, number][];
}

export interface Floor {
	id: string;
	name: string;
	bounds: number[][];
	rooms: Room[];
}

export interface Node {
	id: string;
	name: string;
	point: number[];
	floors: string[];
}

export interface Device {
	id: string;
	name: string;
	nodes: { [index: string]: number }
}

export interface Config {
	timeout: number;
	awayTimeout: number;
	floors: Floor[];
	nodes: Node[];
	devices: Device[];
}