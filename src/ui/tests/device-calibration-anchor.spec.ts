import { expect, test, type Page } from '@playwright/test';
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

	// Height is measured from a floor, and the config and device list both load after the page does
	test.describe('opens an anchored device at its saved height', () => {
		const anchored = { settings: { originalId: 'dev-9', id: 'dev-9', name: 'Anchored', 'rssi@1m': -60, x: 2, y: 3, z: 4.7 }, details: [] };
		const twoFloors = {
			timeout: 30000,
			awayTimeout: 300000,
			floors: [
				{
					id: 'ground',
					name: 'Ground',
					bounds: [
						[0, 0, 0],
						[10, 10, 3]
					],
					rooms: []
				},
				{
					id: 'upstairs',
					name: 'Upstairs',
					bounds: [
						[0, 0, 3],
						[10, 10, 6]
					],
					rooms: []
				}
			],
			devices: [],
			gps: null,
			map: { flipX: false, flipY: false, wallThickness: 1, wallOpacity: 0.35, wallColor: '#64748b' }
		};

		async function open(page: Page, delay: { config?: number; devices?: number }) {
			await mockApi(page, { stubWebSocket: true });
			const payloads: any[] = [];
			await page.route('**/api/device/dev-9', async (route) => {
				if (route.request().method() === 'PUT') {
					payloads.push(route.request().postDataJSON());
					return route.fulfill({ json: {} });
				}
				return route.fulfill({ json: anchored });
			});
			await page.route('**/api/state/config', async (route) => {
				await new Promise((r) => setTimeout(r, delay.config ?? 0));
				await route.fulfill({ json: twoFloors });
			});
			await page.route('**/api/state/devices**', async (route) => {
				await new Promise((r) => setTimeout(r, delay.devices ?? 0));
				await route.fulfill({ json: [] });
			});
			await page.goto('/calibration/devices/dev-9');
			return payloads;
		}

		for (const [name, delay] of [
			['when the config arrives last', { config: 1500 }],
			['when the device list arrives last', { devices: 1500 }]
		] as const) {
			test(name, async ({ page }) => {
				const payloads = await open(page, delay);

				await expect(page.locator('#height-input')).toHaveValue('1.7');
				await page.waitForTimeout(2000); // past the late arrival: nothing may move it

				await expect(page.locator('#height-input')).toHaveValue('1.7');
				await page.getByRole('button', { name: 'Anchor here' }).click();
				await expect.poll(() => payloads.length).toBe(1);
				expect(payloads[0].z).toBeCloseTo(4.7);
			});
		}
	});
});
