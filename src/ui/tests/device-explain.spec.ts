import { expect, test, type Page } from '@playwright/test';
import { mockApi } from './mock-api';

const place = (name: string) => ({ id: name.toLowerCase(), name });

const explanation = {
	id: 'dev-1',
	name: 'Phone',
	anchored: false,
	location: { x: 30, y: 30, z: 1 },
	confidence: 80,
	nodes: [
		{ id: 'node-1', name: 'Node One', current: true, rssi: -60, rssiVar: 1, refRssi: -59, distance: 4, distVar: 0.25, lastHit: null },
		{ id: 'node-2', name: 'Node Two', current: false, rssi: -80, rssiVar: null, refRssi: -59, distance: 9, distVar: null, lastHit: null }
	],
	scenarios: [
		{
			name: 'Nelder-Mead (Main)',
			locator: 'Nelder-Mead',
			floor: place('Main'),
			room: place('Kitchen'),
			best: true,
			current: true,
			probability: 0.75,
			confidence: 80,
			weightedConfidence: 70,
			outcome: 'Solved',
			outcomeReason: null,
			location: { x: 30, y: 30, z: 1 },
			fit: { x: 30, y: 30, z: 1 },
			scale: 1,
			error: 0.1,
			pearsonCorrelation: 0.9,
			iterations: 40,
			reasonForExit: 'Converged',
			fixes: 3,
			nodes: [
				{ id: 'node-1', name: 'Node One', used: true, reason: null, measured: 4, fitted: 3, residual: 1, weight: 1 },
				{ id: 'node-2', name: 'Node Two', used: false, reason: 'Stale', measured: null, fitted: null, residual: null, weight: null }
			]
		}
	],
	floors: [{ floor: place('Main'), probability: 0.75, confidence: 80 }],
	room: {
		room: place('Kitchen'),
		inside: true,
		distanceToEdge: 0.4,
		nearestOtherRoom: place('Hall'),
		nearestOtherDistance: 0.6,
		state: 'Kitchen',
		previousState: 'Hall',
		stateChanged: '2026-10-31T12:00:00Z'
	},
	uncertainty: { center: { x: 30, y: 30, z: 1 }, semiMajor: 3, semiMinor: 1, angleDegrees: 30 },
	hints: ['Node One disagrees most with this fit: it reads 1.6 m long.']
};

const nodes = [
	{ id: 'node-1', name: 'Node One', location: { x: 20, y: 20, z: 1 }, floors: ['1'], nodes: {} },
	{ id: 'node-2', name: 'Node Two', location: { x: 70, y: 70, z: 1 }, floors: ['1'], nodes: {} }
];

async function openDevice(page: Page) {
	await mockApi(page, { stubWebSocket: true, nodes });
	await page.route('**/api/device/dev-1', (route) => route.fulfill({ json: { settings: { id: 'dev-1', originalId: 'dev-1', name: 'Phone' }, details: [] } }));
	await page.route('**/api/device/dev-1/explain', (route) => route.fulfill({ json: explanation }));
	await page.setViewportSize({ width: 1400, height: 900 });
	await page.goto('/devices/dev-1');
}

test('the device page walks through the fix in order', async ({ page }) => {
	await openDevice(page);

	const panel = page.getByTestId('device-explain');
	await expect(panel).toBeVisible();
	await expect(page.getByTestId('explain-hints')).toContainText('disagrees most');
	await expect(panel).toContainText('Nelder-Mead (Main): 75%');
	await expect(panel).toContainText('+1.00 m');
	await expect(panel).toContainText('stale');
	await expect(panel).toContainText('Nearest other room: Hall');
	await expect(panel).toContainText('Was Hall until');
});

test('the map shows the fit: uncertainty, residuals and rejected nodes', async ({ page }) => {
	await openDevice(page);

	await expect(page.getByTestId('fix-ellipse')).toBeAttached();
	await expect(page.locator('svg text', { hasText: '4.00' })).toContainText('(+1.00)');
	await page.getByTestId('device-explain').waitFor();
	await page.screenshot({ path: 'test-results/device-explain.png' });
});

test('a node page draws its links, measured against the map', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true, nodes });
	await page.route('**/api/state/calibration', (route) => route.fulfill({ json: { matrix: { 'Node One': { 'Node Two': { distance: 90, mapDistance: 70.7, percent: 0.27 } } } } }));
	await page.route('**/api/node/node-1', (route) => route.fulfill({ json: { settings: { id: 'node-1', name: 'Node One' }, details: [] } }));
	await page.goto('/nodes/node-1');

	await expect(page.getByTestId('node-pairs').locator('text')).toHaveText('90.0 / 70.7');
});
