import { expect, test, type Page } from '@playwright/test';
import { mockApi } from './mock-api';

const mapSvg = (page: Page) =>
	page
		.locator('svg')
		.filter({ has: page.locator('g') })
		.first();

// Dispatch a wheel event on the map's <svg> and report whether anything called preventDefault on it.
async function wheel(page: Page, init: WheelEventInit): Promise<boolean> {
	return mapSvg(page).evaluate((svg, init) => {
		const rect = svg.getBoundingClientRect();
		const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, clientX: rect.x + rect.width / 2, clientY: rect.y + rect.height / 2, ...init });
		svg.dispatchEvent(event);
		return event.defaultPrevented;
	}, init);
}

// The d3 zoom transform as applied to the room/node/device layers (ignores the cursor coordinate readout).
const snapshot = (page: Page) => mapSvg(page).evaluate((svg) => [...svg.querySelectorAll('g[transform*="scale"]')].map((g) => g.getAttribute('transform')).join('|'));

async function openCalibration(page: Page) {
	await mockApi(page, { stubWebSocket: true });
	await page.route('**/api/device/dev-1', (route) =>
		route.fulfill({
			status: 200,
			contentType: 'application/json',
			body: JSON.stringify({ settings: { originalId: 'dev-1', id: 'dev-1', name: 'Test Device', 'rssi@1m': -60, x: null, y: null, z: null }, details: [] })
		})
	);
	await page.setViewportSize({ width: 1200, height: 900 });
	await page.goto('/calibration/devices/dev-1');
	await page.getByRole('button', { name: 'Anchor here' }).waitFor();
}

test.describe('Map zoom gestures', () => {
	test('full-page map: scroll and pinch both zoom and are claimed from the browser', async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		await page.setViewportSize({ width: 1200, height: 800 });
		await page.goto('/');
		await mapSvg(page).waitFor();

		const before = await snapshot(page);
		expect(await wheel(page, { deltaY: -100 })).toBe(true);
		const afterScroll = await snapshot(page);
		expect(afterScroll).not.toBe(before);

		// Trackpad pinch arrives as a ctrlKey wheel event; the browser must not zoom the page.
		expect(await wheel(page, { deltaY: -10, ctrlKey: true })).toBe(true);
		expect(await snapshot(page)).not.toBe(afterScroll);
	});

	test('embedded map: plain scroll goes to the page, even right after a pinch', async ({ page }) => {
		await openCalibration(page);

		const before = await snapshot(page);
		expect(await wheel(page, { deltaY: 100 })).toBe(false);
		expect(await snapshot(page)).toBe(before);

		// Pinch zooms and is claimed...
		expect(await wheel(page, { deltaY: -10, ctrlKey: true })).toBe(true);
		const zoomed = await snapshot(page);
		expect(zoomed).not.toBe(before);

		// ...and momentum scroll right after it (d3's 150ms wheel gesture is still open) is not swallowed.
		expect(await wheel(page, { deltaY: 100 })).toBe(false);
		expect(await wheel(page, { deltaY: 0 })).toBe(false);
		expect(await snapshot(page)).toBe(zoomed);
	});

	test('Shift+drag draws a box and zooms to it', async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		await page.setViewportSize({ width: 1200, height: 800 });
		await page.goto('/');
		const svg = mapSvg(page);
		await svg.waitFor();
		const box = (await svg.boundingBox())!;
		const x = box.x + box.width / 4;
		const y = box.y + box.height / 4;

		const before = await snapshot(page);
		await page.keyboard.down('Shift');
		await page.mouse.move(x, y);
		await page.mouse.down();
		await page.mouse.move(x + 150, y + 150, { steps: 5 });
		await expect(svg.locator(':scope > rect[stroke-dasharray]')).toHaveCount(1);
		await page.mouse.up();
		await page.keyboard.up('Shift');

		await expect(svg.locator(':scope > rect[stroke-dasharray]')).toHaveCount(0);
		expect(await snapshot(page)).not.toBe(before);
	});

	test('a tiny Shift+click does not zoom', async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		await page.setViewportSize({ width: 1200, height: 800 });
		await page.goto('/');
		const svg = mapSvg(page);
		await svg.waitFor();
		const box = (await svg.boundingBox())!;

		const before = await snapshot(page);
		await page.keyboard.down('Shift');
		await page.mouse.click(box.x + box.width / 3, box.y + box.height / 3);
		await page.keyboard.up('Shift');
		expect(await snapshot(page)).toBe(before);
	});
});
