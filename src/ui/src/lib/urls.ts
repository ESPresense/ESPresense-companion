import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { isNode, type Device, type Node } from '$lib/types';

/**
 * Navigate to the detail page for a Node or Device.
 *
 * If `d` is a Node, navigates to `/nodes/{id}`; otherwise navigates to `/devices/{id}`.
 *
 * @param d - The target Device or Node. If `d` is `null` or has no `id`, the generated URL will include `undefined`.
 */
export function gotoDetail(d: Device | Node | null) {
	if (isNode(d)) goto(resolve('/nodes/[id]', { id: d?.id || 'undefined' }));
	else goto(resolve('/devices/[id]', { id: d?.id || 'undefined' }));
}

/**
 * Navigate to the 3D view for the given device.
 *
 * @param deviceId - The device identifier to include in the path; if `null` the literal `"null"` will be placed in the URL (`/3d/null`).
 */
export function gotoDetail3d(deviceId: string | null) {
	goto(resolve('/3d/[id]', { id: deviceId || 'null' }));
}

/**
 * Navigate to the device calibration page.
 *
 * If `d` is null or `d.id` is undefined, the generated URL will include `undefined` in place of the device id.
 *
 * @param d - The device to calibrate (may be `null`); navigation occurs as a side effect.
 */
export function gotoCalibrateDevice(d: Device | null) {
	goto(resolve('/calibrate/device/[id]', { id: d?.id || 'undefined' }));
}

/**
 * Navigate to the devices list view.
 */
export function gotoDevices() {
	goto(resolve('/devices'));
}
