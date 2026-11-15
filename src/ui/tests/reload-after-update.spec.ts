import { expect, test, type Route } from '@playwright/test';
import { mockApi } from './mock-api';

// An add-on update restarts the server. SvelteKit's own version poll has no timeout and never starts a new check
// while one is in flight, so a single version.json request left hanging during the restart stops it for good. The
// layout's own poll gives up after 4 s and tries again, so the next navigation still does a full page load into
// the new build.
test('a hung version check does not stop the tab from finding the new build', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });

	// real: the running build answers. hang: requests are held open, never answered. new: a newer build answers.
	let phase: 'real' | 'hang' | 'new' = 'real';
	const held: Route[] = [];
	await page.route('**/_app/version.json', (route) => {
		if (phase === 'real') return route.fallback();
		if (phase === 'hang') return void held.push(route);
		return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ version: 'new-build' }) });
	});

	await page.goto('/');
	await expect(page.getByRole('link', { name: 'Calibration' }).first()).toBeVisible();

	phase = 'hang';
	await expect.poll(() => held.length, { timeout: 15000 }).toBeGreaterThan(0);
	phase = 'new';
	await page.waitForTimeout(10000); // the hung check times out (4 s) and the next one (5 s later) sees the new build

	let loads = 0;
	page.on('load', () => loads++);
	await page.getByRole('link', { name: 'Calibration' }).first().click();
	await expect(page).toHaveURL(/\/calibration$/);
	await expect.poll(() => loads, { message: 'the navigation should be a full page load into the new build' }).toBe(1);
});

test('navigates in-app, without a full page load, while the build is unchanged', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.goto('/');
	await expect(page.getByRole('link', { name: 'Calibration' }).first()).toBeVisible();
	await page.waitForTimeout(7000); // at least one version check against the real version.json

	let loads = 0;
	page.on('load', () => loads++);
	await page.getByRole('link', { name: 'Calibration' }).first().click();
	await expect(page).toHaveURL(/\/calibration$/);
	await expect(page.getByTestId('calibration-content')).toBeVisible();
	expect(loads).toBe(0);
});
