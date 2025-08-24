import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test.describe('Devices Table Sorting', () => {
  test('lastSeen column sorts chronologically', async ({ page }) => {
    // Mock devices with different lastSeen timestamps
    const devicesWithDifferentTimestamps = [
      {
        id: 'dev-1',
        name: 'Oldest Device',
        nodes: {},
        room: { id: 'room-1', name: 'Office' },
        floor: { id: '1', name: 'Ground' },
        location: { x: 10, y: 10, z: 1 },
        confidence: 3,
        scale: 1,
        fixes: 1,
        timeout: 30000,
        lastSeen: new Date(Date.now() - 3600000).toISOString() // 1 hour ago
      },
      {
        id: 'dev-2',
        name: 'Newest Device',
        nodes: {},
        room: { id: 'room-1', name: 'Office' },
        floor: { id: '1', name: 'Ground' },
        location: { x: 20, y: 20, z: 1 },
        confidence: 3,
        scale: 1,
        fixes: 1,
        timeout: 30000,
        lastSeen: new Date(Date.now() - 60000).toISOString() // 1 minute ago
      },
      {
        id: 'dev-3',
        name: 'Middle Device',
        nodes: {},
        room: { id: 'room-1', name: 'Office' },
        floor: { id: '1', name: 'Ground' },
        location: { x: 30, y: 30, z: 1 },
        confidence: 3,
        scale: 1,
        fixes: 1,
        timeout: 30000,
        lastSeen: new Date(Date.now() - 1800000).toISOString() // 30 minutes ago
      }
    ];

    // Override the devices endpoint with our test data
    await page.route('**/api/state/devices', (route) => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(devicesWithDifferentTimestamps)
      });
    });

    await mockApi(page, { stubWebSocket: true });
    await page.goto('/devices');

    // Wait for the table to load
    await page.waitForSelector('table');

    // Get initial order (should be by ID initially since that's the default sort)
    const initialRows = await page.$$eval('tbody tr', rows =>
      rows.map(row => ({
        id: row.querySelector('td:first-child')?.textContent?.trim(),
        lastSeenText: row.querySelector('td:nth-child(9)')?.textContent?.trim()
      }))
    );

    // Click the lastSeen column header to sort ascending (oldest first)
    const lastSeenHeader = page.locator('th').filter({ hasText: 'Last Seen' });
    await lastSeenHeader.click();

    // Wait for sort to apply
    await page.waitForTimeout(100);

    // Check that devices are sorted by lastSeen in ascending order (oldest first)
    const sortedAscRows = await page.$$eval('tbody tr', rows =>
      rows.map(row => ({
        id: row.querySelector('td:first-child')?.textContent?.trim(),
        lastSeenText: row.querySelector('td:nth-child(9)')?.textContent?.trim()
      }))
    );

    // Verify the order is chronological (oldest to newest)
    expect(sortedAscRows[0].id).toBe('dev-1'); // Oldest (1 hour ago)
    expect(sortedAscRows[1].id).toBe('dev-3'); // Middle (30 minutes ago)
    expect(sortedAscRows[2].id).toBe('dev-2'); // Newest (1 minute ago)

    // Click again to sort descending (newest first)
    await lastSeenHeader.click();

    // Wait for sort to apply
    await page.waitForTimeout(100);

    // Check that devices are sorted by lastSeen in descending order (newest first)
    const sortedDescRows = await page.$$eval('tbody tr', rows =>
      rows.map(row => ({
        id: row.querySelector('td:first-child')?.textContent?.trim(),
        lastSeenText: row.querySelector('td:nth-child(9)')?.textContent?.trim()
      }))
    );

    // Verify the order is reverse chronological (newest to oldest)
    expect(sortedDescRows[0].id).toBe('dev-2'); // Newest (1 minute ago)
    expect(sortedDescRows[1].id).toBe('dev-3'); // Middle (30 minutes ago)
    expect(sortedDescRows[2].id).toBe('dev-1'); // Oldest (1 hour ago)
  });

  test('lastSeen column shows human-readable time but sorts by date', async ({ page }) => {
    // Mock devices with timestamps that would sort differently alphabetically vs chronologically
    const trickyDevices = [
      {
        id: 'dev-1',
        name: 'Device A',
        nodes: {},
        room: { id: 'room-1', name: 'Office' },
        floor: { id: '1', name: 'Ground' },
        location: { x: 10, y: 10, z: 1 },
        confidence: 3,
        scale: 1,
        fixes: 1,
        timeout: 30000,
        lastSeen: new Date(Date.now() - 86400000).toISOString() // 1 day ago
      },
      {
        id: 'dev-2',
        name: 'Device B',
        nodes: {},
        room: { id: 'room-1', name: 'Office' },
        floor: { id: '1', name: 'Ground' },
        location: { x: 20, y: 20, z: 1 },
        confidence: 3,
        scale: 1,
        fixes: 1,
        timeout: 30000,
        lastSeen: new Date(Date.now() - 3600000).toISOString() // 1 hour ago
      }
    ];

    await page.route('**/api/state/devices', (route) => {
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(trickyDevices)
      });
    });

    await mockApi(page, { stubWebSocket: true });
    await page.goto('/devices');

    // Wait for the table to load
    await page.waitForSelector('table');

    // Click the lastSeen column header to sort ascending (oldest first)
    const lastSeenHeader = page.locator('th').filter({ hasText: 'Last Seen' });
    await lastSeenHeader.click();

    // Wait for sort to apply
    await page.waitForTimeout(100);

    // Get the lastSeen cell text to verify it's displaying human-readable format
    const firstRowLastSeen = await page.$eval('tbody tr:first-child td:nth-child(9)', cell => cell.textContent?.trim());
    const secondRowLastSeen = await page.$eval('tbody tr:nth-child(2) td:nth-child(9)', cell => cell.textContent?.trim());

    // Verify that the display text is human-readable (contains words like "ago")
    expect(firstRowLastSeen).toContain('ago');
    expect(secondRowLastSeen).toContain('ago');

    // Verify that the sorting is chronological, not alphabetical
    // "1 day ago" should come before "1 hour ago" chronologically
    expect(firstRowLastSeen).toContain('day');
    expect(secondRowLastSeen).toContain('hour');
  });
});