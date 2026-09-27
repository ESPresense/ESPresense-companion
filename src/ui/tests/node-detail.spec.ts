import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// Covers the node detail route, whose floorId is local state rather than a route prop
// (SvelteKit route components only receive `data` and `form`).
test('node detail page renders and picks up the node floor', async ({ page }) => {
	const nodes = [
		{
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
		}
	];

	await mockApi(page, { stubWebSocket: true, nodes });

	await page.goto('/nodes/node-1');

	await expect(page.getByText('Detail Node')).toBeVisible();
	// The map only renders once floorId has been resolved from the node's floors.
	await expect(page.locator('svg').first()).toBeVisible();
});
