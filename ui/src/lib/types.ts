
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
  telemetry: {
    version: string;
    ip: string;
  }
  cpu: CPU;
  flavor: Flavor;
  online: boolean;
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
  head_repository: { full_name: string; };
  head_branch: string;
  head_sha: string;
  head_commit: { message: string; };
}

export interface Release {
  assets: Asset[];
  prerelease: boolean;
  tag_name: string;
  name: string;
}
