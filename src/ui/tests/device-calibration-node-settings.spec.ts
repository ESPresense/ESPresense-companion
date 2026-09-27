import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// Regression test: DeviceCalibration used to guard its node-settings fetch on the
// response value (`if (!nodeSettings[node.id])`) while fetchNodeSettings reassigns
// `nodeSettings` to force reactivity. A node whose settings come back null — which
// the Record<string, NodeSetting | null> type allows — never satisfied that guard, so
// the reactive block re-fetched it forever. Each node must be requested once.
test('requests each node settings once even when settings come back null', async ({ page }) => {
	const nodes = [
		{
			id: 'node-1',
			name: 'Node One',
			location: { x: 10, y: 10, z: 1 },
			floors: ['1'],
			nodes: {},
			online: true,
			sourceType: 'Config'
		},
		{
			id: 'node-2',
			name: 'Node Two',
			location: { x: 80, y: 80, z: 1 },
			floors: ['1'],
			nodes: {},
			online: true,
			sourceType: 'Config'
		}
	];

	await mockApi(page, { stubWebSocket: true, nodes });

	await page.route('**/api/device/dev-1', (route) =>
		route.fulfill({
			status: 200,
			contentType: 'application/json',
			body: JSON.stringify({
				settings: { originalId: 'dev-1', id: 'dev-1', name: 'Test Device', 'rssi@1m': -60, x: null, y: null, z: null },
				details: []
			})
		})
	);

	// The case that used to loop: a node with no settings.
	const nodeRequests: string[] = [];
	await page.route('**/api/node/*', (route) => {
		nodeRequests.push(new URL(route.request().url()).pathname);
		return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ settings: null, details: [] }) });
	});

	await page.goto('/calibration/devices/dev-1');

	// Wait until the component has computed node distances and issued a fetch per node.
	await expect(page.getByRole('figure', { name: 'Select node Node One' })).toBeVisible();
	await expect.poll(() => nodeRequests.length).toBeGreaterThanOrEqual(2);

	// Give any runaway loop room to show itself — it produced requests continuously.
	await page.waitForTimeout(2000);

	expect(nodeRequests.filter((p) => p.endsWith('/node-1'))).toHaveLength(1);
	expect(nodeRequests.filter((p) => p.endsWith('/node-2'))).toHaveLength(1);
});
