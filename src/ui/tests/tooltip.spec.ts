import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test.describe('Tooltip', () => {
	test('shows tooltip on hover over calibration matrix cell', async ({ page }) => {
		const calibrationData = {
			matrix: {
				'Node A': {
					'Node B': {
						distance: 5.123,
						rssi: -70,
						mapDistance: 4.567,
						diff: 0.556,
						percent: 0.122,
						rx_adj_rssi: -10,
						absorption: 0.1
					}
				}
			},
			rmse: 0.123,
			r: 0.95
		};

		await mockApi(page, { stubWebSocket: true });

		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.route('**/api/state/calibration/autoOptimize', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ autoOptimize: false })
			});
		});

		await page.goto('/calibration');
		await page.waitForSelector('table');

		// Find the cell with data and hover over it
		const dataCell = page.locator('table tbody td').filter({ hasText: '12%' });
		await dataCell.hover();

		// Wait for tooltip to appear
		const tooltip = page.locator('[role="tooltip"]');
		await expect(tooltip).toBeVisible();

		// Verify tooltip content contains expected data
		const tooltipText = await tooltip.textContent();
		expect(tooltipText).toContain('Map Distance');
		expect(tooltipText).toContain('4.57');
		expect(tooltipText).toContain('Measured');
		expect(tooltipText).toContain('5.12');
		expect(tooltipText).toContain('Error');
		expect(tooltipText).toContain('0.556');
	});

	test('hides tooltip when mouse leaves cell', async ({ page }) => {
		const calibrationData = {
			matrix: {
				'Node A': {
					'Node B': {
						distance: 5.0,
						rssi: -70,
						mapDistance: 4.5,
						diff: 0.5,
						percent: 0.111,
						rx_adj_rssi: -10,
						absorption: 0.1
					}
				}
			},
			rmse: 0.123,
			r: 0.95
		};

		await mockApi(page, { stubWebSocket: true });

		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.route('**/api/state/calibration/autoOptimize', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ autoOptimize: false })
			});
		});

		await page.goto('/calibration');
		await page.waitForSelector('table');

		// Find and hover over the data cell
		const dataCell = page.locator('table tbody td').filter({ hasText: '11%' });
		await dataCell.hover();

		// Verify tooltip is visible
		const tooltip = page.locator('[role="tooltip"]');
		await expect(tooltip).toBeVisible();

		// Move mouse away from the cell
		await page.mouse.move(0, 0);

		// Verify tooltip is hidden
		await expect(tooltip).not.toBeVisible();
	});

	test('shows "No beacon" tooltip for empty cells', async ({ page }) => {
		const calibrationData = {
			matrix: {
				'Node A': {
					'Node B': {
						distance: 5.0,
						rssi: -70,
						mapDistance: 4.5,
						diff: 0.5,
						percent: 0.111
					}
				},
				'Node C': {
					'Node B': null
				}
			},
			rmse: 0.123,
			r: 0.95
		};

		await mockApi(page, { stubWebSocket: true });

		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.route('**/api/state/calibration/autoOptimize', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ autoOptimize: false })
			});
		});

		await page.goto('/calibration');
		await page.waitForSelector('table');

		// Find the empty cell (Node C row, Node B column)
		const rows = page.locator('table tbody tr');
		const nodeCRow = rows.filter({ hasText: 'Tx: Node C' });
		const emptyCell = nodeCRow.locator('td').nth(1); // Second td (first is the name)

		await emptyCell.hover();

		// Wait for tooltip to appear
		const tooltip = page.locator('[role="tooltip"]');
		await expect(tooltip).toBeVisible();

		// Verify tooltip shows "No beacon" message
		const tooltipText = await tooltip.textContent();
		expect(tooltipText).toContain('No beacon');
	});

	test('tooltip has correct accessibility attributes', async ({ page }) => {
		const calibrationData = {
			matrix: {
				'Node A': {
					'Node B': {
						distance: 5.0,
						rssi: -70,
						mapDistance: 4.5,
						diff: 0.5,
						percent: 0.111
					}
				}
			},
			rmse: 0.123,
			r: 0.95
		};

		await mockApi(page, { stubWebSocket: true });

		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.route('**/api/state/calibration/autoOptimize', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify({ autoOptimize: false })
			});
		});

		await page.goto('/calibration');
		await page.waitForSelector('table');

		// Find and hover over the data cell
		const dataCell = page.locator('table tbody td').filter({ hasText: '11%' });
		await dataCell.hover();

		// Verify tooltip has correct ARIA attributes
		const tooltip = page.locator('[role="tooltip"]');
		await expect(tooltip).toBeVisible();
		await expect(tooltip).toHaveAttribute('aria-hidden', 'false');

		// Verify the cell references the tooltip
		const tooltipId = await tooltip.getAttribute('id');
		expect(tooltipId).toMatch(/^tooltip-/);
		await expect(dataCell).toHaveAttribute('aria-describedby', tooltipId!);
	});
});
