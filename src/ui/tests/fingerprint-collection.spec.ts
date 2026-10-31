import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test('collects at the confirmed position and reports insufficient readings', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.route('**/api/device/dev-1', (route) => route.fulfill({ json: { settings: { originalId: 'dev-1', id: 'dev-1', name: 'Test Device' }, details: [] } }));
	let attempts = 0;
	await page.route('**/api/device/dev-1/fingerprint', async (route) => {
		const spot = route.request().postDataJSON();
		expect(route.request().method()).toBe('POST');
		expect(spot.floor).toBeTruthy();
		expect(Number.isFinite(spot.x)).toBe(true);
		expect(Number.isFinite(spot.y)).toBe(true);
		expect(Number.isFinite(spot.z)).toBe(true);
		attempts++;
		await route.fulfill(attempts === 1 ? { status: 400, json: { error: 'Heard 1 nodes; at least 3 are needed.' } } : { json: { sampleId: '00000000-0000-0000-0000-000000000001', nodes: 4, enabled: false, calibration: null } });
	});
	await page.goto('/calibration/devices/dev-1');
	const save = page.getByRole('button', { name: 'Collect sample' });
	await expect(save).toBeEnabled();
	await save.click();
	await expect(page.getByRole('status')).toContainText('at least 3 are needed');
	await save.click();
	await expect(page.getByRole('status')).toContainText('Fingerprint saved from 4 nodes');
	await expect(page.getByRole('status')).toContainText('Enable the fingerprint locator');
	await page.screenshot({ path: '/tmp/fingerprint-collection.png', fullPage: true });
});

for (const fromMap of [false, true]) {
	test(`offers calibration only after sampling, from ${fromMap ? 'map' : 'calibration'}`, async ({ page }) => {
		await mockApi(page, { stubWebSocket: true });
		let settings = { originalId: 'dev-1', id: 'dev-1', name: 'Test Device', 'rssi@1m': -60, x: 10, y: 20, z: 1 };
		const updates: (typeof settings)[] = [];
		await page.route('**/api/device/dev-1', async (route) => {
			if (route.request().method() === 'PUT') {
				updates.push(route.request().postDataJSON());
				settings = updates[updates.length - 1];
			}
			await route.fulfill({ json: { settings, details: [] } });
		});
		let removed = '';
		await page.route('**/api/device/dev-1/fingerprint/*', async (route) => {
			expect(route.request().method()).toBe('DELETE');
			removed = route.request().url().split('/').pop()!;
			await route.fulfill({ status: 204 });
		});
		let samples = 0;
		await page.route('**/api/device/dev-1/fingerprint', async (route) => {
			samples++;
			await route.fulfill({ json: { sampleId: `00000000-0000-0000-0000-${String(samples).padStart(12, '0')}`, nodes: 4, enabled: true, calibration: -65 } });
		});
		await page.goto(fromMap ? '/devices/dev-1' : '/calibration/devices/dev-1');
		if (fromMap) await page.getByRole('button', { name: "I'm here", exact: true }).click();
		const apply = page.getByRole('button', { name: 'Apply suggested calibration' });
		await expect(apply).toHaveCount(0);
		await page.getByRole('button', { name: 'Collect sample' }).click();
		await expect(apply).toBeVisible();
		expect(updates).toHaveLength(0);
		await page.getByRole('button', { name: 'Keep current calibration' }).click();
		await expect(apply).toHaveCount(0);
		expect(updates).toHaveLength(0);
		await page.getByRole('button', { name: 'Collect sample' }).click();
		await expect(apply).toBeVisible();
		// Another editor changed the name since the page loaded. Applying must preserve it.
		settings = { ...settings, name: 'Renamed Device' };
		await apply.click();
		await expect(page.getByText('Calibration updated. Your saved fingerprint is unchanged.')).toBeVisible();
		expect(updates).toHaveLength(1);
		expect(updates[0]).toMatchObject({ name: 'Renamed Device', 'rssi@1m': -65, x: 10, y: 20, z: 1 });
		expect(samples).toBe(2);
		await page.getByRole('button', { name: 'Undo last sample' }).click();
		await expect(page.getByRole('status')).toContainText('Last fingerprint sample removed');
		expect(removed).toBe('00000000-0000-0000-0000-000000000002');
		expect(updates).toHaveLength(1); // Undo must not revert an applied calibration.
		await expect(page.getByRole('button', { name: 'Undo last sample' })).toHaveCount(0);
		if (fromMap) {
			await page.getByRole('button', { name: 'Close teaching' }).click();
			await page.getByRole('button', { name: "I'm here", exact: true }).click();
			await page.getByRole('button', { name: 'Collect sample' }).click();
			await expect(page.getByText('Device calibration (RSSI at 1 m): -65 → -65 dBm.')).toBeVisible();
		}
		if (fromMap) await page.screenshot({ path: '/tmp/teach-location-map.png', fullPage: true });
	});
}

test('undo can be retried after an error and discards the sample calibration suggestion', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	await page.route('**/api/device/dev-1', (route) => route.fulfill({ json: { settings: { id: 'dev-1', originalId: 'dev-1' }, details: [] } }));
	const sampleId = '00000000-0000-0000-0000-000000000001';
	await page.route('**/api/device/dev-1/fingerprint', (route) => route.fulfill({ json: { sampleId, nodes: 3, enabled: false, calibration: -65 } }));
	let attempts = 0;
	await page.route(`**/api/device/dev-1/fingerprint/${sampleId}`, async (route) => {
		attempts++;
		await route.fulfill(attempts === 1 ? { status: 500, json: { error: 'Could not update the fingerprint file.' } } : { status: 204 });
	});
	await page.goto('/calibration/devices/dev-1');
	await page.getByRole('button', { name: 'Collect sample' }).click();
	await expect(page.getByRole('button', { name: 'Apply suggested calibration' })).toBeVisible();
	await page.getByRole('button', { name: 'Undo last sample' }).click();
	await expect(page.getByRole('status')).toContainText('Could not update the fingerprint file');
	await page.getByRole('button', { name: 'Undo last sample' }).click();
	await expect(page.getByRole('status')).toContainText('Last fingerprint sample removed');
	await expect(page.getByRole('button', { name: 'Apply suggested calibration' })).toHaveCount(0);
});
