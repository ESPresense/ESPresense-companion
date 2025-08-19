import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test('index page has title', async ({ page }) => {
        await mockApi(page);
        await page.goto('/');
        await expect(page).toHaveTitle(/ESPresense Companion/);
});
