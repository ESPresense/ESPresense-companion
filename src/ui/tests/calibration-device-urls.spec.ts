import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// Characters that would otherwise end the path segment, start a query or fragment, or be mis-decoded.
const id = 'name:a/b#c?d%e';
const encoded = encodeURIComponent(id);

test('a device ID with URL-sensitive characters survives calibration navigation, loading and capture polling', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.route('**/api/state/devices**', (route) =>
		route.fulfill({
			json: [{ id, name: 'Encoded Device', nodes: {}, floor: null, location: null, lastSeen: new Date().toISOString(), timeout: 30000 }]
		})
	);

	const paths: string[] = [];
	await page.route(/\/api\/device\//, (route) => {
		const url = new URL(route.request().url());
		paths.push(url.pathname + url.search);
		if (url.pathname !== `/api/device/${encoded}`) return route.fulfill({ status: 204 });
		return route.fulfill({
			json: {
				settings: { originalId: id, id, name: 'Encoded Device', 'rssi@1m': -60 },
				details: [{ key: 'Device ID', value: id }]
			}
		});
	});

	await page.goto('/calibration');
	await page.getByRole('button', { name: 'Devices', exact: true }).click();
	await page.getByRole('row').filter({ hasText: 'Encoded Device' }).getByText('Not Calibrated', { exact: true }).click();

	await expect(page).toHaveURL((url) => url.pathname === `/calibration/devices/${encoded}` && url.hash === '' && url.search === '');
	await expect(page.getByRole('textbox', { name: 'Device ID', exact: true })).toHaveValue(id);
	// Route load and details polling, plus the calibration component's own capture request.
	await expect.poll(() => paths.filter((p) => p === `/api/device/${encoded}`).length).toBeGreaterThanOrEqual(2);
	await expect.poll(() => paths.includes(`/api/device/${encoded}/capture`)).toBe(true);
	expect(paths.every((p) => p.startsWith(`/api/device/${encoded}`))).toBe(true);
});
