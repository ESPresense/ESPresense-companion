import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const ws = vi.hoisted(() => ({
	open: new Set<() => void>(),
	sendMessage: vi.fn()
}));
const apiFetch = vi.hoisted(() => vi.fn());

vi.mock('#lib/api.js', () => ({ apiFetch }));
vi.mock('./wsManager', () => ({
	WSManager: class {
		sendMessage = ws.sendMessage;
		subscribeToEvent() {}
		unsubscribeFromEvent() {}
		onOpen(callback: () => void) {
			ws.open.add(callback);
			return () => ws.open.delete(callback);
		}
	}
}));

const { devices } = await import('./stores');

const calls = (path: string) => apiFetch.mock.calls.filter(([url]) => String(url).startsWith(path)).length;
const reconnect = () => ws.open.forEach((callback) => callback());

describe('devices store reconnect', () => {
	let stop: () => void;

	beforeEach(() => {
		apiFetch.mockReset();
		ws.sendMessage.mockReset();
	});
	afterEach(() => stop?.());

	it('re-sends the filter and refreshes devices and config on reconnect', async () => {
		apiFetch.mockImplementation(async (url: string) => (url.startsWith('/api/state/devices') ? [{ id: 'dev-1' }] : {}));
		let value: unknown[] = [];
		stop = devices.subscribe((v) => (value = v));
		await vi.waitFor(() => expect(value).toHaveLength(1));
		apiFetch.mockClear();
		ws.sendMessage.mockClear();

		reconnect();

		expect(ws.sendMessage).toHaveBeenCalledWith({ command: 'changeFilter', type: 'showAll', value: 'false' });
		expect(ws.sendMessage).toHaveBeenCalledWith({ command: 'changeFilter', type: 'include', value: '' });
		await vi.waitFor(() => {
			expect(calls('/api/state/devices')).toBe(1);
			expect(calls('/api/state/config')).toBe(1);
		});
	});

	it('refetches after an in-flight poll when a reconnect lands during it', async () => {
		let release!: (devices: unknown[]) => void;
		apiFetch.mockImplementation((url: string) => (url.startsWith('/api/state/devices') ? new Promise((resolve) => (release = resolve)) : Promise.resolve({})));
		stop = devices.subscribe(() => {});
		await vi.waitFor(() => expect(calls('/api/state/devices')).toBe(1));

		reconnect();
		expect(calls('/api/state/devices')).toBe(1);

		release([]);
		await vi.waitFor(() => expect(calls('/api/state/devices')).toBe(2));
	});
});
