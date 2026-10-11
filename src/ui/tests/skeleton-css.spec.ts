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
		const setButton = page.getByRole('button', { name: 'Set', exact: true });
		await setButton.waitFor();

		// btn + preset-filled-* must actually paint; unresolved presets leave it transparent.
		await expect(setButton).not.toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');

		// The height field and its Set button are a single flush control: same row,
		// no gap. This is what Skeleton v4's `input-group` used to provide.
		const field = await heightInput.boundingBox();
		const button = await setButton.boundingBox();
		expect(field).not.toBeNull();
		expect(button).not.toBeNull();
		expect(Math.abs(field!.y - button!.y)).toBeLessThan(2);
		expect(Math.abs(field!.x + field!.width - button!.x)).toBeLessThan(2);
	});
});
