import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// The readout is mouse-owned state. It was declared as `$: cursorX = 0`, i.e. derived,
// which is wrong in intent and left it liable to be reset by a reactive re-run.
test('coordinate readout tracks the mouse and holds its value', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.goto('/');

	const readout = page.getByText(/^X: .*, Y: /);
	await expect(readout).toBeVisible();
	await expect(readout).toHaveText('X: 0, Y: 0');

	const svg = page.locator('svg').first();
	const box = await svg.boundingBox();
	if (!box) throw new Error('map svg has no box');

	await page.mouse.move(box.x + box.width * 0.6, box.y + box.height * 0.6);

	await expect(readout).not.toHaveText('X: 0, Y: 0');
	const moved = await readout.textContent();

	// The value must survive subsequent component updates rather than snapping back to 0.
	await page.waitForTimeout(1500);
	await expect(readout).toHaveText(moved!);
});

// The readout is what Ctrl+C copies into config.yaml room points, so it must be exact:
// the mock device marker sits at (30, 30) and hovering its centre has to read that.
test('coordinate readout is accurate at a known point', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.setViewportSize({ width: 1200, height: 800 });
	await page.goto('/');

	const svg = page
		.locator('svg')
		.filter({ has: page.locator('g') })
		.first();
	await svg.waitFor();
	const marker = (await svg.locator('circle').first().boundingBox())!;
	await page.mouse.move(marker.x + marker.width / 2, marker.y + marker.height / 2);

	const readout = page.getByText(/^X: .*, Y: /);
	await expect(readout).toHaveText(/^X: 30(\.[01])?, Y: 30(\.[01])?$|^X: 29\.9, Y: 29\.9$/);
});
