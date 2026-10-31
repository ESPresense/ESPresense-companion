import type { ScaleLinear } from 'd3';

/** The floor map's LayerCake scale types: `getLayerCakeContext<MapScales>()`. Read values off the context (`chart.xScale`), don't destructure. */
export type MapScales = { x: ScaleLinear<number, number, never>; y: ScaleLinear<number, number, never> };

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
		board?: string | null;
	};
	cpu: CPU;
	flavor: Flavor;
	online: boolean;
	updateError?: string | null;
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
	'rssi@1m'?: number | null;
	'measuredRssi@1m': number | null;
	isAnchored?: boolean;
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
	x?: number | null;
	y?: number | null;
	z?: number | null;
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
	anchored?: string[];
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

/** Matches the backend DeviceHistory row (flat coordinates, one row per scenario). */
export interface DeviceHistory {
	id: string;
	when: string;
	x: number | null;
	y: number | null;
	z: number | null;
	confidence: number;
	fixes: number;
	scenario: string | null;
	best: boolean;
}

// Mirrors DeviceExplanation in src/Models/DeviceExplanation.cs
export type ExplainedPoint = { x: number; y: number; z: number };
export type ExplainedPlace = { id: string | null; name: string | null };

export type ExplainedReading = {
	id: string | null;
	name: string | null;
	current: boolean;
	rssi: number;
	rssiVar: number | null;
	refRssi: number;
	distance: number;
	distVar: number | null;
	lastHit: string | null;
};

export type ExplainedNode = {
	id: string | null;
	name: string | null;
	used: boolean;
	reason: string | null;
	measured: number | null;
	fitted: number | null;
	residual: number | null;
	weight: number | null;
};

export type ExplainedScenario = {
	name: string;
	locator: string;
	floor: ExplainedPlace | null;
	room: ExplainedPlace | null;
	best: boolean;
	current: boolean;
	probability: number;
	confidence: number | null;
	weightedConfidence: number;
	outcome: 'Solved' | 'Fallback' | 'NoFix' | null;
	outcomeReason: string | null;
	location: ExplainedPoint;
	fit: ExplainedPoint | null;
	scale: number | null;
	error: number | null;
	pearsonCorrelation: number | null;
	iterations: number | null;
	reasonForExit: string;
	fixes: number | null;
	nodes: ExplainedNode[];
};

export type ExplainedFloor = { floor: ExplainedPlace; probability: number; confidence: number | null };

export type ExplainedRoom = {
	room: ExplainedPlace | null;
	inside: boolean;
	distanceToEdge: number | null;
	nearestOtherRoom: ExplainedPlace | null;
	nearestOtherDistance: number | null;
	state: string | null;
	previousState: string | null;
	stateChanged: string | null;
};

export type ExplainedEllipse = { center: ExplainedPoint; semiMajor: number; semiMinor: number; angleDegrees: number };

export type DeviceExplanation = {
	id: string;
	name: string | null;
	anchored: boolean;
	location: ExplainedPoint | null;
	confidence: number | null;
	nodes: ExplainedReading[];
	scenarios: ExplainedScenario[];
	floors: ExplainedFloor[];
	room: ExplainedRoom;
	uncertainty: ExplainedEllipse | null;
	hints: string[];
};

export type NodeSelfTest = { id: string; name: string | null; error: number | null; linkError: number | null; neighbours: number };

// Mirrors PositionAccuracy in src/Models/PositionAccuracy.cs
export type PositionAccuracy = {
	timestamp: string;
	placed: number;
	solved: number;
	unsolved: number;
	median: number | null;
	p95: number | null;
	worst: NodeSelfTest | null;
	guidance: string;
	nodes: NodeSelfTest[];
	hints: string[];
};
