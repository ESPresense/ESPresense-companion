import { base } from '$app/paths';
import { goto } from '$app/navigation';
import { isNode, type Device, type Node } from '$lib/types';

/**
 * Navigate to the detail page for a Node or Device.
 *
 * If `d` is a Node, navigates to `${base}/nodes/{id}`; otherwise navigates to `${base}/devices/{id}`.
 *
 * @param d - The target Device or Node. If `d` is `null` or has no `id`, the generated URL will include `undefined`.
 */
export function detail(d: Device | Node | null) {
	if (isNode(d)) goto(`${base}/nodes/${d?.id}`);
	else goto(`${base}/devices/${d?.id}`);
}

/**
 * Navigate to the 3D view for the given device.
 *
 * @param deviceId - The device identifier to include in the path; if `null` the literal `"null"` will be placed in the URL (`.../3d/null`).
 */
export function detail3d(deviceId: string | null) {
	goto(`${base}/3d/${deviceId}`);
}

/**
 * Navigate to a device's detail page with the calibration tab selected.
 *
 * If `d` is null or `d.id` is undefined, the generated URL will include `undefined` in place of the device id.
 *
 * @param d - The device to calibrate (may be `null`); navigation occurs as a side effect.
 */
export function calibrateDevice(d: Device | null) {
	// Navigate to the device detail page with the calibration tab selected
	goto(`${base}/devices/${d?.id}?tab=calibration`);
}
