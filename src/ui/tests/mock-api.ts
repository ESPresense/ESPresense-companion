import type { Page } from '@playwright/test';

export async function mockApi(page: Page) {
  await page.addInitScript(() => {
    const respond = (data: unknown) =>
      Promise.resolve(
        new Response(JSON.stringify(data), {
          status: 200,
          headers: { 'Content-Type': 'application/json' }
        })
      );

    // @ts-ignore - override global fetch for tests
    window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;

      if (url.endsWith('/api/state/config')) {
        return respond({
          floors: [{ id: '1', bounds: [ [0, 0, 0], [100, 100, 0] ] }],
          map: {}
        });
      }
      if (url.includes('/api/state/devices')) {
        return respond([]);
      }
      if (url.includes('/api/state/nodes')) {
        return respond([]);
      }
      if (url.includes('/api/state/calibration')) {
        return respond({ matrix: {} });
      }
      return respond([]);
    };
  });
}
