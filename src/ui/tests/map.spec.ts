import { test, expect } from '@playwright/test';
import { mockApi } from './mock-api';

test('map renders with LayerCake', async ({ page }) => {
  await mockApi(page, { stubWebSocket: true });
  // Ensure the app has a real layout size so the SVG can size > 100px
  await page.addStyleTag({ content: 'html, body { height: 100vh; width: 100vw; } #svelte { height: 100%; width: 100%; }' });
  await page.goto('/');
  await page.locator('svg').first().waitFor({ state: 'visible' });
  await expect(async () => {
    const boxes = await page.locator('svg').evaluateAll((els) =>
      els.map((e) => ({ w: e.clientWidth, h: e.clientHeight }))
    );
    if (!boxes.some((b) => b.w > 100 && b.h > 100)) {
      throw new Error('map svg not found');
    }
  }).toPass();
});
