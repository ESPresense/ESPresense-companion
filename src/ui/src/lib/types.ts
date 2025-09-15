import type { ScaleOrdinal, ScaleLinear } from 'd3';
import type { Writable } from 'svelte/store';

export interface LayerCakeContext {
	xScale: Writable<ScaleLinear<number, number, never>>;
	yScale: Writable<ScaleLinear<number, number, never>>;
	width: Writable<number>;
	height: Writable<number>;
	padding: Writable<{
		top: number;
		right: number;
		bottom: number;
		left: number;
	}>;
	colors: ScaleOrdinal<string, string>;
}

export interface Room {
	id: string;
	name: string;
	points: [number, number][];
	// Optional display color for this room (e.g., "#1f77b4")
	color?: string;
}

export interface Floor {
	id: string;
	name: string;
	bounds: number[][];
	rooms: Room[];
}

export interface Node {
	telemetry: {
		version: string;
		ip: string;
	};
	cpu: CPU;
	flavor: Flavor;
	online: boolean;
	id: string;
	name: string;
	location: { x: number; y: number; z: number };
	floors: string[];
	nodes: { [index: string]: { dist: number; var: number; lh: number } };
	sourceType: 'Config' | 'Discovered';
}

export interface Device {
	id: string;
	name: string;
	nodes: { [index: string]: { dist: number; var: number; lh: number } };
	room: { id: string; name: string };
	floor: { id: string; name: string };
	location: { x: number; y: number; z: number };
	confidence: number;
	scale: number;
	fixes: number;
	timeout: number;
	lastSeen: Date;
	'rssi@1m': number | null;
	'measuredRssi@1m': number | null;
}

export interface MapConfig {
	flipX: boolean;
	flipY: boolean;
	wallThickness: number;
	wallColor?: string;
	wallOpacity?: number;
}

export interface ConfigGps {
	latitude?: number | null;
	longitude?: number | null;
	elevation?: number | null;
	rotation?: number | null; // Degrees clockwise from North
	report?: boolean; // Include GPS coordinates when reporting to Home Assistant
}

export interface Config {
	timeout: number;
	awayTimeout: number;
	floors: Floor[];
	devices: Device[];
	gps?: ConfigGps | null;
	map: MapConfig;
}

export type NodeSetting = {
	id: string | null;
	name: string | null;
	updating: {
		autoUpdate: boolean | null;
		prerelease: boolean | null;
	};
	scanning: {
		forgetAfterMs: number | null;
	};
	counting: {
		idPrefixes: string | null;
		minDistance: number | null;
		maxDistance: number | null;
		minMs: number | null;
	};
	filtering: {
		includeIds: string | null;
		excludeIds: string | null;
		maxDistance: number | null;
		skipDistance: number | null;
		skipMs: number | null;
	};
	calibration: {
		absorption: number | null;
		rxRefRssi: number | null;
		rxAdjRssi: number | null;
		txRefRssi: number | null;
	};
};

export type NodeSettingDetails = {
	settings: NodeSetting | null; // Match C# nullability
	details: Array<{ key: string; value: string }>; // Corresponds to IList<KeyValuePair<string, string>>
};

export type DeviceSetting = {
	originalId: string;
	id: string | null;
	name: string | null;
	'rssi@1m': number | null;
	error?: string;
};

export type DeviceSettingsDetails = {
	settings: DeviceSetting | null;
	details: Array<{ key: string; value: string }>;
};

export interface DeviceMessage {
	distance: number;
	rssi: number;
	'rssi@1m': number;
	name?: string;
	var?: number;
}

export type Firmware = {
	name: string;
	cpu: string;
	flavor: string;
};

export type Flavor = {
	name: string;
	value: string;
	cpus: string[];
};

export type CPU = {
	name: string;
	value: string;
};

export type FirmwareManifest = {
	firmware: Firmware[];
	flavors: Flavor[];
	cpus: CPU[];
};

export interface PullRequest {
	id: number;
	url: string;
}

export interface Asset {
	id: number;
	name: string;
}

export interface WorkflowRun {
	id: number;
	pull_requests: PullRequest[];
	head_repository: { full_name: string };
	head_branch: string;
	head_sha: string;
	head_commit: { message: string };
	status: string;
	created_at: string;
}

export interface Release {
	assets: Asset[];
	prerelease: boolean;
	tag_name: string;
	name: string;
}

export interface NodeCalibrationMatrix {
	[txName: string]: {
		[rxName: string]: {
			tx_ref_rssi?: number;
			rx_adj_rssi?: number;
			absorption?: number;
			mapDistance: number;
			distance: number;
			rssi: number;
			diff: number;
			percent: number;
			var?: number;
		};
	};
}

export interface OptimizerState {
	optimizers: string;
	bestRMSE?: number;
	bestR?: number;
}

export interface CalibrationResponse {
	matrix: NodeCalibrationMatrix;
	rmse?: number;
	r?: number;
	optimizerState?: OptimizerState;
}

/**
 * Determines if the provided object is a Node.
 *
 * This type guard checks if the object has a defined telemetry property,
 * confirming it as a Node rather than a Device or null.
 *
 * @param d - Object to check (can be a Device, Node, or null).
 * @returns True if the object is a Node; otherwise, false.
 */
export function isNode(d: Device | Node | null): d is Node {
	return (d as Node)?.telemetry !== undefined;
}

export interface DeviceHistory {
	id: string;
	when: string;
	location: { x: number; y: number; z: number };
	confidence?: number;
	fixes?: number;
	scale?: number;
	room?: string;
	floor?: string;
}
