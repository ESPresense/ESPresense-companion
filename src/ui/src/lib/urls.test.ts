import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Device, Node } from './types';
import { gotoCalibration, gotoDetail, gotoDetail3d, gotoDevice } from './urls';

const navigation = vi.hoisted(() => ({ goto: vi.fn() }));

vi.mock('$app/navigation', () => ({ goto: navigation.goto }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => `/base/${path.replace(/^\//, '')}` }));

// Characters that would otherwise end the path segment, start a query or fragment, or be mis-decoded.
const id = 'name:a/b#c?d%e';
const encoded = 'name%3Aa%2Fb%23c%3Fd%25e';

describe('device URLs keep the whole ID in one path segment', () => {
	beforeEach(() => navigation.goto.mockClear());

	it.each([
		['gotoCalibration(string)', () => gotoCalibration(id), `/base/calibration/devices/${encoded}`],
		['gotoCalibration(device)', () => gotoCalibration({ id } as Device), `/base/calibration/devices/${encoded}`],
		['gotoDevice', () => gotoDevice(id), `/base/devices/${encoded}`],
		['gotoDetail(device)', () => gotoDetail({ id } as Device), `/base/devices/${encoded}`],
		['gotoDetail(node)', () => gotoDetail({ id, telemetry: {} } as unknown as Node), `/base/nodes/${encoded}`],
		['gotoDetail3d', () => gotoDetail3d(id), `/base/3d/${encoded}`]
	])('%s', (_name, go, expected) => {
		go();
		expect(navigation.goto).toHaveBeenCalledExactlyOnceWith(expected);
	});

	it.each([undefined, null, '', { id: '' } as Device])('gotoCalibration opens the overview without an ID: %j', (target) => {
		gotoCalibration(target);
		expect(navigation.goto).toHaveBeenCalledExactlyOnceWith('/base/calibration');
	});
});
