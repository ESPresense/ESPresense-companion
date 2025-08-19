import { test, expect } from '@playwright/test';
import { mockApi } from './mock-api';

test('map renders with LayerCake', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');
  await expect(async () => {
    const boxes = await page.locator('svg').evaluateAll((els) =>
      els.map((e) => ({ w: e.clientWidth, h: e.clientHeight }))
    );
    if (!boxes.some((b) => b.w > 100 && b.h > 100)) {
      throw new Error('map svg not found');
    }
  }).toPass();
});
