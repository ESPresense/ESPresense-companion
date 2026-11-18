import { expect, test, type Page } from '@playwright/test';
import { mockApi } from './mock-api';

// A small two-room plan, metres, so the markers are big enough on screen to look at.
const floor = {
	id: '1',
	name: 'Ground',
	bounds: [
		[0, 0, 0],
		[10, 6, 2.5]
	],
	rooms: [
		{
			id: 'room-1',
			name: 'Office',
			points: [
				[0, 0],
				[5, 0],
				[5, 6],
				[0, 6]
			]
		},
		{
			id: 'room-2',
			name: 'Lab',
			points: [
				[5, 0],
				[10, 0],
				[10, 6],
				[5, 6]
			]
		}
	]
};

const now = Date.now();
const ago = (seconds: number) => new Date(now - seconds * 1000).toISOString();
const device = (id: string, x: number, lastSeenSecondsAgo: number, extra: Record<string, unknown> = {}) => ({
	id,
	name: id,
	nodes: {},
	room: x < 5 ? { id: 'room-1', name: 'Office' } : { id: 'room-2', name: 'Lab' },
	floor: { id: '1', name: 'Ground' },
	location: { x, y: 3, z: 1 },
	confidence: 80,
	scale: 1,
	fixes: 4,
	timeout: 30000,
	lastSeen: ago(lastSeenSecondsAgo),
	...extra
});
const devices = [device('dev-1', 2, 0), device('quiet', 4, 25), device('anchor', 7.5, 0, { isAnchored: true, confidence: 100 })];

// Ten minutes ago in the Lab, walking back to the Office where dev-1 is now.
const fix = (secondsAgo: number, x: number) => ({ id: 'dev-1', when: ago(secondsAgo), x, y: 3, z: 1, confidence: 50, fixes: 3, scenario: 'MultiFloor', best: true });
const rows = [fix(600, 8.5), fix(450, 7), fix(300, 5.5), fix(120, 3.5), fix(30, 2)];

async function open(page: Page, path: string, historyEnabled: boolean) {
	await mockApi(page, { stubWebSocket: true, history: { enabled: historyEnabled }, floors: [floor], devices });
	await page.route('**/api/history/dev-1/range**', (route) => route.fulfill({ json: { history: rows } }));
	await page.setViewportSize({ width: 1400, height: 900 });
	await page.addStyleTag({ content: 'html, body { height: 100vh; width: 100vw; } #svelte { height: 100%; width: 100%; }' });
	await page.goto(path);
	await page.locator('canvas').first().waitFor({ state: 'visible' });
}

test('marks anchored and quiet devices in the legend', async ({ page }) => {
	await open(page, '/3d', false);
	const legend = page.getByTestId('map3d-legend');
	await expect(legend).toContainText('Pin: an anchored device');
	await expect(legend).toContainText('Faded: not heard from lately');
	await expect(legend).not.toContainText('Trail');
	await page.screenshot({ path: 'test-results/map3d-devices.png' });
});

test('the device 3D page shares the history scrubber and shows where the device was', async ({ page }) => {
	await open(page, '/3d/dev-1', true);

	await expect(page.getByTestId('history-slider')).toBeVisible();
	await expect(page.getByTestId('map3d-legend')).toContainText('Trail');
	await expect(page.getByTestId('map3d-history-time')).toHaveCount(0);
	await page.screenshot({ path: 'test-results/map3d-history-live.png' });

	const slider = page.getByTestId('history-slider');
	const start = Number(await slider.getAttribute('min'));
	await slider.fill(String(start + 200_000));

	await expect(page.getByTestId('history-where')).toContainText('Lab');
	await expect(page.getByTestId('map3d-history-time')).toHaveText(await page.getByTestId('history-time').innerText());
	await page.screenshot({ path: 'test-results/map3d-history-scrubbed.png' });

	await page.getByRole('button', { name: 'Live' }).click();
	await expect(page.getByTestId('map3d-history-time')).toHaveCount(0);
});

test('says so on the device 3D page when history is not being recorded', async ({ page }) => {
	await open(page, '/3d/dev-1', false);
	await expect(page.getByTestId('device-history')).toContainText("History isn't being recorded");
	await expect(page.getByTestId('map3d-legend')).not.toContainText('Trail');
});
