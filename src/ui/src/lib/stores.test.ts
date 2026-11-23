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

const { devices, relative } = await import('./stores');

const calls = (path: string) => apiFetch.mock.calls.filter(([url]) => String(url).startsWith(path)).length;
const reconnect = () => ws.open.forEach((callback) => callback());

describe('devices store reconnect', () => {
	let stop: () => void;

	beforeEach(() => {
		apiFetch.mockReset();
		ws.sendMessage.mockReset();
	});
	afterEach(() => {
		stop?.();
		vi.useRealTimers();
	});

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

	it.each([false, true])('coalesces a reconnect during a later poll: %s', async (withReconnect) => {
		vi.useFakeTimers();
		apiFetch.mockImplementation(async (url: string) => (url.startsWith('/api/state/devices') ? [] : {}));
		stop = devices.subscribe(() => {});
		// Complete the initial fetch and the follow-up caused by synchronous filter subscriptions.
		await vi.advanceTimersByTimeAsync(0);
		expect(calls('/api/state/devices')).toBe(2);
		apiFetch.mockClear();

		let release!: (devices: unknown[]) => void;
		apiFetch.mockImplementation((url: string) => (url.startsWith('/api/state/devices') ? new Promise((resolve) => (release = resolve)) : Promise.resolve({})));
		await vi.advanceTimersByTimeAsync(60000);
		expect(calls('/api/state/devices')).toBe(1);
		if (withReconnect) reconnect();
		expect(calls('/api/state/devices')).toBe(1);
		release([]);
		await vi.advanceTimersByTimeAsync(0);
		expect(calls('/api/state/devices')).toBe(withReconnect ? 2 : 1);
		// Complete the coalesced request too, keeping every lifecycle self-contained.
		if (withReconnect) release([]);
		await vi.advanceTimersByTimeAsync(0);
	});

	it('does not publish or refetch after the last subscriber leaves', async () => {
		vi.useFakeTimers();
		let release!: (devices: unknown[]) => void;
		apiFetch.mockImplementation(() => new Promise((resolve) => (release = resolve)));
		const published = vi.fn();
		stop = devices.subscribe(published);
		stop();
		published.mockClear();
		release([{ id: 'stale' }]);
		await vi.advanceTimersByTimeAsync(0);
		expect(calls('/api/state/devices')).toBe(1);
		expect(published).not.toHaveBeenCalled();
	});

	it('does not let an old lifecycle overwrite a restarted store', async () => {
		vi.useFakeTimers();
		let releaseOld!: (devices: unknown[]) => void;
		apiFetch.mockImplementation(() => new Promise((resolve) => (releaseOld = resolve)));
		stop = devices.subscribe(() => {});
		stop();
		apiFetch.mockImplementation(async () => [{ id: 'fresh' }]);
		let value: { id: string }[] = [];
		stop = devices.subscribe((v) => (value = v));
		await vi.advanceTimersByTimeAsync(0);
		expect(value.map((d) => d.id)).toEqual(['fresh']);
		const before = calls('/api/state/devices');
		releaseOld([{ id: 'stale' }]);
		await vi.advanceTimersByTimeAsync(0);
		expect(value.map((d) => d.id)).toEqual(['fresh']);
		expect(calls('/api/state/devices')).toBe(before);
	});
});

// This module owns a timer independently of the devices lifecycle.
relative.stop();
