
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