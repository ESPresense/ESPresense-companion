import { expect, test, type Page } from '@playwright/test';
import { mockApi } from './mock-api';

// #1730: an untracked bare MAC used to be reachable only via "Show All". Ctrl+K searches every
// device server-side, so the page never has to load or render the full list.
const all = [
	{ id: 'a4c138a4048c', name: null, tracked: false, room: null, floor: null, nodes: 8, nearestNode: 'kitchen', nearestDistance: 2.97, lastSeen: new Date().toISOString() },
	{ id: 'tv:family', name: 'Family TV', tracked: true, room: 'Family', floor: 'Ground', nodes: 3, nearestNode: 'family', nearestDistance: 1.2, lastSeen: new Date().toISOString() }
];

async function open(page: Page) {
	await mockApi(page, { stubWebSocket: true });
	const queries: string[] = [];
	await page.route('**/api/state/devices/search**', (route) => {
		const q = (new URL(route.request().url()).searchParams.get('q') ?? '').toLowerCase();
		queries.push(q);
		const hits = all.filter((d) => !q || d.id.includes(q) || (d.name ?? '').toLowerCase().includes(q));
		return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(hits) });
	});
	await page.goto('/devices');
	await expect(page.locator('table tbody tr').first()).toBeVisible();
	return queries;
}

const box = (page: Page) => page.getByRole('combobox', { name: 'Find a device' });

test('Ctrl+K opens the palette, Escape closes it', async ({ page }) => {
	await open(page);
	await expect(box(page)).toHaveCount(0);

	await page.keyboard.press('Control+k');
	await expect(box(page)).toBeFocused();

	await page.keyboard.press('Escape');
	await expect(box(page)).toHaveCount(0);
});

test('Escape hands focus back to the sidebar button that opened it', async ({ page }) => {
	await open(page);
	const button = page.getByRole('button', { name: 'Find a device' });
	await button.click();
	await expect(box(page)).toBeFocused();

	await page.keyboard.press('Escape');
	await expect(button).toBeFocused();
});

test('the sidebar search button opens it too', async ({ page }) => {
	await open(page);
	await page.getByRole('button', { name: 'Find a device' }).click();
	await expect(box(page)).toBeFocused();
});

test('finds an untracked MAC with its nearest node, and flags it untracked', async ({ page }) => {
	await open(page);
	await page.keyboard.press('Control+k');
	await box(page).fill('a4c138');

	const options = page.getByRole('option');
	await expect(options).toHaveCount(1);
	await expect(options.first()).toContainText('a4c138a4048c');
	await expect(options.first()).toContainText('8 nodes · nearest kitchen 2.97 m');
	await expect(options.first()).toContainText('untracked');
});

test('arrow keys move the selection and Enter opens that device', async ({ page }) => {
	await open(page);
	await page.keyboard.press('Control+k');
	await expect(page.getByRole('option')).toHaveCount(2);
	await expect(page.getByRole('option').first()).toHaveAttribute('aria-selected', 'true');

	await page.keyboard.press('ArrowDown');
	await expect(page.getByRole('option').nth(1)).toHaveAttribute('aria-selected', 'true');

	await page.keyboard.press('Enter');
	await expect(page).toHaveURL(/\/devices\/tv:family$/);
	await expect(box(page)).toHaveCount(0);
});

test('says so when nothing matches', async ({ page }) => {
	await open(page);
	await page.keyboard.press('Control+k');
	await box(page).fill('zzzz');
	await expect(page.getByText('No devices match.')).toBeVisible();
});
