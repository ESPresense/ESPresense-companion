import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test.describe('Calibration Matrix Anchored Devices', () => {
	test('excludes empty receiver columns for anchored devices', async ({ page }) => {
		// Mock calibration data with anchored devices
		const calibrationData = {
			matrix: {
				// Regular node with receiver data
				"Regular Node": {
					"Receiver Node": {
						distance: 5.0,
						rssi: -70,
						mapDistance: 4.5,
						diff: 0.5,
						percent: 0.111,
						rx_adj_rssi: -10,
						absorption: 0.1
					}
				},
				// Anchored device as transmitter with receiver data
				"Test Anchor": {
					"Receiver Node": {
						distance: 4.1,
						rssi: -65,
						mapDistance: 4.0,
						diff: 0.1,
						percent: 0.025,
						rx_adj_rssi: -10,
						absorption: 0.1
					}
				},
				// Regular transmitter that would show anchored device as receiver (empty)
				"TX Node": {
					"Anchored Device": {
						// This would be empty/null in real data, but let's test with empty object
					}
				}
			},
			rmse: 0.123,
			r: 0.95
		};

		await mockApi(page, { stubWebSocket: true });

		// Override the calibration endpoint with our test data
		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.goto('/calibration');

		// Wait for the calibration matrix to load
		await page.waitForSelector('table');

		// Get all column headers
		const columnHeaders = await page.$$eval('table thead th', (headers) =>
			headers.slice(1).map((header) => header.textContent?.trim()) // Skip first "Name" column
		);

		// Should include "Receiver Node" since it has actual data
		expect(columnHeaders).toContain('Rx: Receiver Node');

		// Should NOT include "Anchored Device" since it has no actual measurement data
		expect(columnHeaders).not.toContain('Rx: Anchored Device');

		// Verify we have the expected number of receiver columns (only those with data)
		const receiverColumns = columnHeaders.filter(header => header?.startsWith('Rx:'));
		expect(receiverColumns).toHaveLength(1); // Only "Receiver Node"

		// Verify transmitter rows are still present
		const transmitterRows = await page.$$eval('table tbody tr', (rows) =>
			rows.map((row) => row.querySelector('td:first-child')?.textContent?.trim())
		);

		expect(transmitterRows).toContain('Tx: Regular Node');
		expect(transmitterRows).toContain('Tx: Test Anchor');
		expect(transmitterRows).toContain('Tx: TX Node');
	});

	test('includes receiver columns when anchored devices have actual measurement data', async ({ page }) => {
		// Mock calibration data where an anchored device actually has receiver data
		const calibrationData = {
			matrix: {
				"Regular Node": {
					"Anchored Device": {
						distance: 3.0,
						rssi: -60,
						mapDistance: 2.8,
						diff: 0.2,
						percent: 0.071,
						rx_adj_rssi: -5
					},
					"Regular Receiver": {
						distance: 5.0,
						rssi: -70,
						mapDistance: 4.5,
						diff: 0.5,
						percent: 0.111
					}
				}
			},
			rmse: 0.456,
			r: 0.88
		};

		await mockApi(page, { stubWebSocket: true });

		// Override the calibration endpoint with our test data
		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.goto('/calibration');

		// Wait for the calibration matrix to load
		await page.waitForSelector('table');

		// Get all column headers
		const columnHeaders = await page.$$eval('table thead th', (headers) =>
			headers.slice(1).map((header) => header.textContent?.trim()) // Skip first "Name" column
		);

		// Should include both receivers since they both have actual data
		expect(columnHeaders).toContain('Rx: Anchored Device');
		expect(columnHeaders).toContain('Rx: Regular Receiver');

		// Verify we have the expected number of receiver columns
		const receiverColumns = columnHeaders.filter(header => header?.startsWith('Rx:'));
		expect(receiverColumns).toHaveLength(2);

		// Verify the data is displayed correctly in the matrix
		const firstRowCells = await page.$$eval('table tbody tr:first-child td', (cells) =>
			cells.slice(1).map((cell) => cell.textContent?.trim()) // Skip first "Name" cell
		);

		// Should have data for both receivers
		expect(firstRowCells).toHaveLength(2);
		expect(firstRowCells[0]).not.toBe(''); // Anchored Device column should have data
		expect(firstRowCells[1]).not.toBe(''); // Regular Receiver column should have data
	});

	test('handles matrix with no receiver data gracefully', async ({ page }) => {
		// Mock calibration data with only transmitters, no receivers
		const calibrationData = {
			matrix: {
				"Standalone Transmitter": {},
				"Another Transmitter": {}
			},
			rmse: null,
			r: null
		};

		await mockApi(page, { stubWebSocket: true });

		// Override the calibration endpoint with our test data
		await page.route('**/api/state/calibration', (route) => {
			route.fulfill({
				status: 200,
				contentType: 'application/json',
				body: JSON.stringify(calibrationData)
			});
		});

		await page.goto('/calibration');

		// Wait for the calibration matrix to load
		await page.waitForSelector('table');

		// Get all column headers
		const columnHeaders = await page.$$eval('table thead th', (headers) =>
			headers.slice(1).map((header) => header.textContent?.trim()) // Skip first "Name" column
		);

		// Should have no receiver columns
		const receiverColumns = columnHeaders.filter(header => header?.startsWith('Rx:'));
		expect(receiverColumns).toHaveLength(0);

		// But should still show transmitter rows
		const transmitterRows = await page.$$eval('table tbody tr', (rows) =>
			rows.map((row) => row.querySelector('td:first-child')?.textContent?.trim())
		);

		expect(transmitterRows).toContain('Tx: Standalone Transmitter');
		expect(transmitterRows).toContain('Tx: Another Transmitter');
	});
});