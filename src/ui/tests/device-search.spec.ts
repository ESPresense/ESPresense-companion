import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// The motivating case (#1730): an unnamed BLE device shows up only as a raw MAC, and
// before this there was no way to find it except toggling "Show All" and scrolling.
const devices = [
	{
		id: 'a4c138a4048c',
		name: null,
		nodes: {},
		room: null,
		floor: null,
		location: null,
		confidence: null,
		scale: null,
		fixes: 8,
		timeout: 30000,
		lastSeen: new Date().toISOString()
	},
	{
		id: 'tv:family',
		name: 'Family TV',
		nodes: {},
		room: { id: 'room-1', name: 'Family' },
		floor: { id: '1', name: 'Ground' },
		location: { x: 2.7, y: 4.9, z: 1.5 },
		confidence: 100,
		scale: 1,
		fixes: 9,
		timeout: 30000,
		lastSeen: new Date().toISOString()
	},
	{
		id: 'keys:darrell',
		name: "Darrell's Keys",
		nodes: {},
		room: { id: 'room-2', name: 'Office' },
		floor: { id: '1', name: 'Ground' },
		location: { x: 6.4, y: 5.4, z: 0.9 },
		confidence: 63,
		scale: 1,
		fixes: 9,
		timeout: 30000,
		lastSeen: new Date().toISOString()
	}
];

async function openDevices(page) {
	await mockApi(page, { stubWebSocket: true });
	await page.route('**/api/state/devices**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(devices) }));
	await page.goto('/devices');
	await expect(page.locator('table tbody tr').first()).toBeVisible();
}

const bodyRows = (page) => page.locator('table tbody tr');

test('filters an unnamed device down by its raw MAC', async ({ page }) => {
	await openDevices(page);
	await expect(bodyRows(page)).toHaveCount(3);

	await page.getByRole('searchbox').fill('a4c138');

	await expect(bodyRows(page)).toHaveCount(1);
	await expect(bodyRows(page).first()).toContainText('a4c138a4048c');
});

test('matches on name and on room, and clears back to everything', async ({ page }) => {
	await openDevices(page);
	const box = page.getByRole('searchbox');

	await box.fill('family tv'); // name
	await expect(bodyRows(page)).toHaveCount(1);
	await expect(bodyRows(page).first()).toContainText('Family TV');

	await box.fill('office'); // room column, opted in via `searchable`
	await expect(bodyRows(page)).toHaveCount(1);
	await expect(bodyRows(page).first()).toContainText("Darrell's Keys");

	await box.fill('');
	await expect(bodyRows(page)).toHaveCount(3);
});

test('terms are AND-ed, and a miss says so rather than showing an empty table', async ({ page }) => {
	await openDevices(page);
	const box = page.getByRole('searchbox');

	await box.fill('keys office'); // both must match
	await expect(bodyRows(page)).toHaveCount(1);

	await box.fill('keys family'); // name from one row, room from another
	await expect(page.getByText(/Nothing matches/)).toBeVisible();
});

test('shows a filtered count so you know rows are hidden', async ({ page }) => {
	await openDevices(page);
	await expect(page.getByText('3', { exact: true })).toBeVisible();
	await page.getByRole('searchbox').fill('a4c138');
	await expect(page.getByText('1 of 3')).toBeVisible();
});
