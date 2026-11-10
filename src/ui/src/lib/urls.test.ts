import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Device } from './types';
import { gotoCalibration } from './urls';

const navigation = vi.hoisted(() => ({ base: '', goto: vi.fn() }));

vi.mock('$app/navigation', () => ({ goto: navigation.goto }));
vi.mock('$app/paths', () => ({
	resolve: (path: string) => `${navigation.base}/${path.replace(/^\//, '')}`
}));

const ids = [
	['name:tag#one', 'name%3Atag%23one'],
	['name:tag?one', 'name%3Atag%3Fone'],
	['name:tag%one', 'name%3Atag%25one'],
	['name:tag/one', 'name%3Atag%2Fone'],
	['name:tag%2Fone', 'name%3Atag%252Fone'],
	['irk:aa:bb:cc', 'irk%3Aaa%3Abb%3Acc']
];

describe.each(['', '/ingress/companion'])('calibration navigation with base "%s"', (base) => {
	beforeEach(() => {
		navigation.base = base;
		navigation.goto.mockClear();
	});

	it.each(ids)('encodes the raw string ID %s as one path segment', (id, encoded) => {
		gotoCalibration(id);
		expect(navigation.goto).toHaveBeenCalledExactlyOnceWith(`${base}/calibration/devices/${encoded}`);
	});

	it.each(ids)('encodes the device object ID %s as one path segment', (id, encoded) => {
		gotoCalibration({ id } as Device);
		expect(navigation.goto).toHaveBeenCalledExactlyOnceWith(`${base}/calibration/devices/${encoded}`);
	});

	it.each([undefined, null, '', { id: '' } as Device, {} as Device])('opens the overview for an absent ID: %j', (target) => {
		gotoCalibration(target);
		expect(navigation.goto).toHaveBeenCalledExactlyOnceWith(`${base}/calibration`);
	});
});
