import { resolve } from '$app/paths';
import { goto } from '$app/navigation';
import { isNode, type Device, type Node } from '$lib/types';

/**
 * Navigate to the detail page for a Node or Device.
 *
 * If `d` is a Node, navigates to `/nodes/{id}`; otherwise navigates to `/devices/{id}`.
 *
 * @param d - The target Device or Node. If `d` is `null` or has no `id`, the generated URL will include `undefined`.
 */
export function gotoDetail(d: Device | Node | null) {
	if (isNode(d)) goto(resolve(`/nodes/${d?.id ?? ''}`));
	else goto(resolve(`/devices/${d?.id ?? ''}`));
}

/**
 * Navigate to the 3D view for the given device.
 *
 * @param deviceId - The device identifier to include in the path; if `null` the literal `"null"` will be placed in the URL (`.../3d/null`).
 */
export function gotoDetail3d(deviceId: string | null) {
	goto(resolve(`/3d/${deviceId ?? ''}`));
}

/**
 * Navigate to a device's calibration view.
 *
 * If `d` is null or `d.id` is undefined, the generated URL will include `undefined` in place of the device id.
 *
 * @param d - The device to calibrate (may be `null`); navigation occurs as a side effect.
 */
export function gotoMap() {
	goto(resolve('/'));
}

export function gotoDevices() {
	goto(resolve('/devices'));
}

export function gotoNodes() {
	goto(resolve('/nodes'));
}

export function gotoCalibration(target?: Device | string | null) {
	const id = typeof target === 'string' ? target : target?.id;
	goto(resolve(id ? `/calibration/devices/${id}` : '/calibration'));
}
