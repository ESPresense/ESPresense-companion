
export interface Room {
	id: string;
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
	nodes: { [index: string]: number };
	room: { id: string, name: string };
	floor: { id: string, name: string };
  location: { x: number, y: number, z: number };
  confidence: number;
  scale: number;
  fixes: number;
  lastHit: Date;
}

export interface Config {
	timeout: number;
	awayTimeout: number;
	floors: Floor[];
	nodes: Node[];
	devices: Device[];
}

export type DeviceSetting = {
  originalId: string;
  id: string | null;
  name: string | null;
  "rssi@1m": number | null;
 };