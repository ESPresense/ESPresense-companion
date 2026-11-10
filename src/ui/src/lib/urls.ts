import { resolve } from '$app/paths';
import { goto } from '$app/navigation';
import { isNode, type Device, type Node, type SearchResult } from '#lib/types.js';

/**
 * Navigate to the detail page for a Node or Device.
 *
 * If `d` is a Node, navigates to `/nodes/{id}`; otherwise navigates to `/devices/{id}`.
 *
 * @param d - The target Device or Node. If `d` is `null` or has no `id`, the generated URL will include `undefined`.
 */
export function gotoDetail(d: Device | Node | null) {
	if (isNode(d)) goto(resolve(`nodes/${d?.id ?? ''}`));
	else goto(resolve(`devices/${d?.id ?? ''}`));
}

/** Navigate to the page a search result leads to. */
export function gotoSearchResult(r: SearchResult) {
	const id = encodeURIComponent(r.id);
	if (r.kind === 'node') goto(resolve(`nodes/${id}`));
	else goto(resolve(`devices/${id}`));
}

/**
 * Navigate to the 3D view for the given device.
 *
 * @param deviceId - The device identifier to include in the path; if `null` the literal `"null"` will be placed in the URL (`.../3d/null`).
 */
export function gotoDetail3d(deviceId: string | null) {
	goto(resolve(`3d/${deviceId ?? ''}`));
}

export function gotoMap() {
	goto(resolve('/'));
}

export function gotoDevices() {
	goto(resolve('devices'));
}

export function gotoNodes() {
	goto(resolve('nodes'));
}

/** Navigate to a device's map page. */
export function gotoDevice(deviceId: string) {
	goto(resolve(`devices/${encodeURIComponent(deviceId)}`));
}

/** Navigate to a device's calibration page, or the calibration overview when no device is given. */
export function gotoCalibration(target?: Device | string | null) {
	const id = typeof target === 'string' ? target : target?.id;
	goto(id ? resolve(`calibration/devices/${encodeURIComponent(id)}`) : resolve('/calibration'));
}
