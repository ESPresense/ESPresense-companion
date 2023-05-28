import { base } from '$app/paths';
import { goto } from '$app/navigation';
import type { Device } from '$lib/types';

export function detail(d: Device | null) {
  goto(`${base}/devices/${d?.id}`);
}