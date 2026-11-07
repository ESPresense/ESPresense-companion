import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// Skeleton is a CSS-only dependency for most of the app. Tailwind silently drops
// unknown utility classes, so a Skeleton utility that gets removed or renamed in a
// major bump produces no build error, no type error, and no test failure — just a
// silent visual regression. These assert computed style rather than class names.
test.describe('Skeleton CSS layer', () => {
	test('preset and form utilities resolve on the calibration page', async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		await page.route('**/api/device/dev-1', (route) =>
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ settings: { originalId: 'dev-1', id: 'dev-1', name: 'Test Device', 'rssi@1m': -60, x: 1, y: 2, z: 1 }, details: [] })
			})
		);

		await page.goto('/calibration/devices/dev-1');

		const heightInput = page.locator('#height-input');
		const floorSelect = page.locator('#floor-select');
		const anchorButton = page.getByRole('button', { name: 'Anchor here', exact: true });
		await heightInput.waitFor();
		await anchorButton.waitFor();

		// btn + preset-filled-* must actually paint; unresolved presets leave it transparent.
		await expect(anchorButton).not.toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');

		// card + preset-tonal is the surface the floor/height controls sit on.
		const controlsCard = page.locator('.card.preset-tonal').filter({ has: floorSelect });
		await expect(controlsCard).not.toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');

		// The floor select and the height input share one compact row, bottoms aligned
		// (flex-wrap + items-end), with the select to the left of the input.
		const select = await floorSelect.boundingBox();
		const field = await heightInput.boundingBox();
		expect(select).not.toBeNull();
		expect(field).not.toBeNull();
		expect(Math.abs(select!.y + select!.height - (field!.y + field!.height))).toBeLessThan(2);
		expect(select!.x + select!.width).toBeLessThanOrEqual(field!.x);
	});
});
