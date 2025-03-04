import { base } from '$app/paths';
import { goto } from '$app/navigation';
import { isNode, type Device, type Node } from '$lib/types';

export function detail(d: Device | Node | null) {
	if (isNode(d)) goto(`${base}/nodes/${d?.id}`);
	else goto(`${base}/devices/${d?.id}`);
}

export function calibrate(d: Device | null) {
	goto(`${base}/calibration/${d?.id}`);
}

export function calibrateById(id: string) {
	goto(`${base}/calibration/${id}`);
}
