import { expect, test } from '@playwright/test';

async function failOnConsoleErrors(page) {
	page.on('console', msg => {
		if (msg.type() === 'error') {
			console.error(`Console error: ${msg.text()}`);
			throw new Error(`Console error: ${msg.text()}`);
		}
	});
}

test('navigate to Map', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("Map")');
	await expect(page).toHaveURL(/\/$/);
});

test('navigate to 3D View', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("3D View")');
	await expect(page).toHaveURL(/\/3d$/);
});

test('navigate to Devices', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("Devices")');
	await expect(page).toHaveURL(/\/devices$/);
});

test('navigate to Nodes', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("Nodes")');
	await expect(page).toHaveURL(/\/nodes$/);
});

test('navigate to Calibration', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("Calibration")');
	await expect(page).toHaveURL(/\/calibration$/);
});

test('navigate to Geolocation', async ({ page }) => {
	await failOnConsoleErrors(page);
	await page.goto('/');
	await page.click('a:has-text("Geolocation")');
	await expect(page).toHaveURL(/\/geolocation$/);
});
