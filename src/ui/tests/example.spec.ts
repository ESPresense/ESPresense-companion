import { expect, test } from '@playwright/test';

test('nodes page has expected heading', async ({ page }) => {
        await page.goto('/nodes');
        await expect(page.getByRole('heading', { name: 'Nodes' })).toBeVisible();
});
