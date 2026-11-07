import { expect, test, type Page } from '@playwright/test';
import { mockApi } from './mock-api';

const now = Date.now();
const at = (secondsAgo: number) => new Date(now - secondsAgo * 1000).toISOString();
const fix = (secondsAgo: number, x: number, extra: Record<string, unknown> = {}) => ({ id: 'dev-1', when: at(secondsAgo), x, y: 30, z: 0, confidence: 5, fixes: 3, scenario: 'MultiFloor', best: true, ...extra });

// Ten minutes ago the device sat in the Lab, then crossed to the Office, where it is now.
const rows = [fix(600, 70), fix(600, 65, { scenario: 'NearestNode', best: false, confidence: 2 }), fix(300, 50), fix(30, 30)];

async function openDevice(page: Page, enabled: boolean) {
	await mockApi(page, { stubWebSocket: true, history: { enabled } });
	await page.route('**/api/device/dev-1', (route) => route.fulfill({ json: { settings: { id: 'dev-1', originalId: 'dev-1', name: 'Phone' }, details: [] } }));
	await page.route('**/api/device/dev-1/explain', (route) => route.fulfill({ status: 404 }));
	await page.route('**/api/history/dev-1/range**', (route) => route.fulfill({ json: { history: rows } }));
	await page.setViewportSize({ width: 1400, height: 900 });
	await page.goto('/devices/dev-1');
}

test('says so when history is not being recorded', async ({ page }) => {
	await openDevice(page, false);

	await expect(page.getByTestId('device-history')).toContainText("History isn't being recorded");
	await expect(page.getByTestId('history-slider')).toHaveCount(0);
	await expect(page.getByTestId('history-trail')).toHaveCount(0);
});

test('draws the trail behind the live marker, and scrubbing shows where the device was', async ({ page }) => {
	await openDevice(page, true);

	await expect(page.getByTestId('history-point')).toHaveCount(3);
	await expect(page.getByTestId('history-ghost')).toHaveCount(0);
	await expect(page.getByTestId('history-where')).toContainText('Office');

	const slider = page.getByTestId('history-slider');
	const start = Number(await slider.getAttribute('min'));
	await slider.fill(String(start + 1000));

	await expect(page.getByTestId('history-ghost')).toBeVisible();
	await expect(page.getByTestId('history-where')).toContainText('Lab');
	await expect(page.getByTestId('history-point')).toHaveCount(1);
	await page.screenshot({ path: 'test-results/device-history.png' });

	await page.getByRole('button', { name: 'Live' }).click();
	await expect(page.getByTestId('history-ghost')).toHaveCount(0);
	await expect(page.getByTestId('history-point')).toHaveCount(3);
});
