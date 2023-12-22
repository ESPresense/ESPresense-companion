import type { ScaleOrdinal, ScaleLinear } from "d3";

export interface LayerCakeContext {
  xScale: SvelteStore<ScaleLinear<number, number, never>>;
  yScale: SvelteStore<ScaleLinear<number, number, never>>;
  colors: ScaleOrdinal<string, string>
}

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
  location: { x: number, y: number, z: number };
  floors: string[];
  nodes: { [index: string]: { dist: number, var: number, lh: number } };
}

export interface Device {
  id: string;
  name: string;
  nodes: { [index: string]: { dist: number, var: number, lh: number } };
  room: { id: string, name: string };
  floor: { id: string, name: string };
  location: { x: number, y: number, z: number };
  confidence: number;
  scale: number;
  fixes: number;
  timeout: number;
  lastHit: Date;
}

export interface Config {
  timeout: number;
  awayTimeout: number;
  floors: Floor[];
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
  status: string;
  created_at: string;
}

export interface Release {
  assets: Asset[];
  prerelease: boolean;
  tag_name: string;
  name: string;
}

export function isNode(d: Device | Node | null): d is Node {
  return (d as Node)?.telemetry !== undefined;
}