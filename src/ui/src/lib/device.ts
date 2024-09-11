import { base } from '$app/paths';
import type { DeviceSetting, DeviceDetail } from './types';

export async function fetchDeviceSettings(fetch, id: string): Promise<DeviceSetting> {
    const response = await fetch(`${base}/api/device/${id}/settings`);
    if (!response.ok) throw new Error("Something went wrong loading device details (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function fetchDeviceDetails(id: string): Promise<DeviceDetail> {
    const response = await fetch(`${base}/api/device/${id}/details`);
    if (!response.ok) throw new Error("Something went wrong loading device details (error="+response.status+" "+response.statusText+")");
    return await response.json();
}
