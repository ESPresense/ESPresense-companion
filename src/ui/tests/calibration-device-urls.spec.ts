import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

for (const id of ['name:tag#one', 'name:tag?one', 'name:tag%one', 'name:tag/one', 'name:tag%2Fone', 'irk:aa:bb:cc']) {
	test(`preserves ${id} through calibration navigation, loading and polling`, async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		await page.route('**/api/state/devices**', (route) =>
			route.fulfill({
				json: [{ id, name: 'Encoded Device', nodes: {}, floor: null, location: null, lastSeen: new Date().toISOString(), timeout: 30000 }]
			})
		);

		const encoded = encodeURIComponent(id);
		const expectedPath = `/api/device/${encoded}`;
		const requests: URL[] = [];
		await page.route(/\/api\/device\//, (route) => {
			const url = new URL(route.request().url());
			requests.push(url);
			// The component also polls capture status, separately from the details under test.
			if (url.pathname !== expectedPath) return route.fulfill({ status: 204 });
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
		// The first device request comes from the route loader.
		expect(requests[0].pathname).toBe(expectedPath);
		expect(requests[0].search).toBe('');
		// One route load, one immediate details request, then at least one interval poll.
		await expect.poll(() => requests.filter((url) => url.pathname === expectedPath && url.search === '' && url.hash === '').length).toBeGreaterThanOrEqual(3);
	});
}
