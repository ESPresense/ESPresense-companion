import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test.describe('Device Calibration Anchoring', () => {
	test('anchors and clears a device location', async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });

		const deviceResponse = {
			settings: {
				originalId: 'dev-1',
				id: 'dev-1',
				name: 'Test Device',
				'rssi@1m': -60,
				x: null,
				y: null,
				z: null
			},
			details: []
		};

		const anchorPayloads: any[] = [];

		await page.route('**/api/device/dev-1', async (route) => {
			const method = route.request().method();
			if (method === 'GET') {
				await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(deviceResponse) });
				return;
			}

			if (method === 'PUT') {
				const body = route.request().postDataJSON();
				anchorPayloads.push(body);
				await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
				return;
			}

			await route.continue();
		});

		await page.goto('/calibration/devices/dev-1');

		const anchorButton = page.getByRole('button', { name: 'Anchor here' });
		await anchorButton.waitFor();
		await expect(anchorButton).toBeEnabled();

		await anchorButton.click();
		await expect.poll(() => anchorPayloads.length).toBe(1);
		expect(typeof anchorPayloads[0].x).toBe('number');
		expect(typeof anchorPayloads[0].y).toBe('number');
		expect(typeof anchorPayloads[0].z).toBe('number');

		const removeAnchorButton = page.getByRole('button', { name: 'Remove anchor' });
		await removeAnchorButton.waitFor();
		await removeAnchorButton.click();

		await expect.poll(() => anchorPayloads.length).toBe(2);
		expect(anchorPayloads[1].x).toBeNull();
		expect(anchorPayloads[1].y).toBeNull();
		expect(anchorPayloads[1].z).toBeNull();
	});
});
