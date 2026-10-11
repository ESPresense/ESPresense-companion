import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

// Regression: nodes that aren't placed in the config come back with `floors: null`.
// `n?.floors.includes(floorId)` guarded only `n`, so as soon as a floor was selected the
// map's node filter threw once per poll tick ("Cannot read properties of null").
test('map survives nodes that have no floors assigned', async ({ page }) => {
	const errors: string[] = [];
	page.on('pageerror', (e) => errors.push(e.message));

	await mockApi(page, {
		stubWebSocket: true,
		nodes: [
			{
				id: 'placed',
				name: 'Placed Node',
				location: { x: 10, y: 10, z: 1 },
				floors: ['1'],
				nodes: {},
				online: true,
				sourceType: 'Config'
			},
			{
				// Discovered but not placed — exactly what the backend returns for these.
				id: 'unplaced',
				name: 'Unplaced Node',
				location: { x: 0, y: 0, z: 0 },
				floors: null,
				nodes: {},
				online: true,
				sourceType: 'Mqtt'
			}
		]
	});

	await page.goto('/');

	// The floor tabs select a floor, which is what makes the filter evaluate `floors`.
	await expect(page.getByRole('figure', { name: 'Select node Placed Node' })).toBeVisible();

	// Give the 1 Hz nodes poll a few ticks to re-run the filter.
	await page.waitForTimeout(2500);

	expect(errors).toEqual([]);
});
