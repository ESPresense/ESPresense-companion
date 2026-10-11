import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

const node = {
	id: 'node-1',
	name: 'Detail Node',
	telemetry: { version: '1.0.0', ip: '192.168.1.5' },
	cpu: { value: 'esp32', name: 'ESP32' },
	flavor: { value: '', name: 'Standard', cpus: ['esp32'] },
	online: true,
	location: { x: 10, y: 10, z: 0 },
	floors: ['1'],
	nodes: {},
	sourceType: 'Config'
};

// Covers the node detail route, whose floorId is local state rather than a route prop
// (SvelteKit route components only receive `data` and `form`).
test('node detail page renders and picks up the node floor', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true, nodes: [node] });

	await page.goto('/nodes/node-1');

	await expect(page.getByText('Detail Node')).toBeVisible();
	// The map only renders once floorId has been resolved from the node's floors.
	await expect(page.locator('svg').first()).toBeVisible();
});

// Regression: the details poll built its URL from the nodes store, which is empty on first
// paint, so it requested /api/node/undefined once before the store resolved.
test('details poll never requests an undefined node id', async ({ page }) => {
	const requested: string[] = [];
	await mockApi(page, { stubWebSocket: true, nodes: [node] });
	page.on('request', (r) => {
		const path = new URL(r.url()).pathname;
		if (path.startsWith('/api/node/')) requested.push(path);
	});

	await page.goto('/nodes/node-1');
	await expect(page.getByText('Detail Node')).toBeVisible();
	// Let the 1 Hz poll run a few times.
	await page.waitForTimeout(2500);

	expect(requested.length).toBeGreaterThan(0);
	expect(requested.filter((p) => p.includes('undefined'))).toEqual([]);
});
