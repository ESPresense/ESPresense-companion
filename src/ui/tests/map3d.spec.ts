import { test, expect } from '@playwright/test';
import { mockApi } from './mock-api';

test.describe('3D view', () => {
  test.beforeEach(async ({ page }) => {
    await mockApi(page, { stubWebSocket: true });
    // Ensure the app has a real layout size so WebGL canvas can size > 100px
    await page.addStyleTag({ content: 'html, body { height: 100vh; width: 100vw; } #svelte { height: 100%; width: 100%; }' });
  });

  test('renders canvas and labels on /3d', async ({ page }) => {
    await page.goto('/3d');

    // Wait for WebGL canvas to be visible
    const canvas = page.locator('canvas');
    await canvas.first().waitFor({ state: 'visible' });

    // Verify canvas has a sensible size
    await expect(async () => {
      const boxes = await canvas.evaluateAll((els) => els.map((e) => ({ w: e.clientWidth, h: e.clientHeight })));
      if (!boxes.some((b) => b.w > 100 && b.h > 100)) throw new Error('3D canvas not sized');
    }).toPass();

    // CSS2DRenderer overlay should be present
    await expect(page.locator('.css2d-renderer-map')).toBeVisible();
  });

  test('renders device view on /3d/dev-1', async ({ page }) => {
    await page.goto('/3d/dev-1');

    // Wait for WebGL canvas to be visible
    const canvas = page.locator('canvas');
    await canvas.first().waitFor({ state: 'visible' });

    // Verify canvas has a sensible size
    await expect(async () => {
      const boxes = await canvas.evaluateAll((els) => els.map((e) => ({ w: e.clientWidth, h: e.clientHeight })));
      if (!boxes.some((b) => b.w > 100 && b.h > 100)) throw new Error('3D canvas not sized');
    }).toPass();

    // Ensure we aren't in the "not found" branch
    await expect(page.getByText(/not found/i)).toHaveCount(0);
  });
});

