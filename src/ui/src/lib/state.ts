import { base } from '$app/paths';
import type { Config, CalibrationData, Device } from './types';

export async function fetchConfig(): Promise<Config> {
    const response = await fetch(`${base}/api/state/config`);
    if (!response.ok) throw new Error("Something went wrong loading config (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function fetchCalibrationState(): Promise<CalibrationData> {
    const response = await fetch(`${base}/api/state/calibration`);
    if (!response.ok) throw new Error("Something went wrong loading calibration state (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function fetchDeviceState(): Promise<Device[]> {
    const response = await fetch(`${base}/api/state/devices`);
    if (!response.ok) throw new Error("Something went wrong loading device state (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function fetchNodeState(includeTele: boolean = true): Promise<Node[]> {
    const response = await fetch(`${base}/api/state/nodes?includeTele=${includeTele}`);
    if (!response.ok) throw new Error("Something went wrong loading node state (error="+response.status+" "+response.statusText+")");
    return await response.json();
}