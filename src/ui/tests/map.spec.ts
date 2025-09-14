import { test, expect } from '@playwright/test';
import { mockApi } from './mock-api';

test('map renders with LayerCake', async ({ page }) => {
	await mockApi(page, { stubWebSocket: true });
	
	// Set viewport and ensure proper layout
	await page.setViewportSize({ width: 1200, height: 800 });
	
	// Ensure the app has a real layout size so the SVG can size > 100px
	await page.addStyleTag({ 
		content: `
			html, body { 
				height: 100vh; 
				width: 100vw; 
				margin: 0; 
				padding: 0;
			} 
			#svelte { 
				height: 100%; 
				width: 100%; 
			}
			.layer-cake-container {
				min-height: 400px;
				min-width: 400px;
			}
		` 
	});
	
	await page.goto('/');
	
	// Wait for the config to load first
	await page.waitForFunction(() => {
		return window.fetch && window.document.body.children.length > 0;
	});
	
	// Wait for SVG to appear
	await page.locator('svg').first().waitFor({ state: 'visible', timeout: 10000 });
	
	// Give the LayerCake component time to size itself properly
	await page.waitForTimeout(1000);
	
	await expect(async () => {
		const boxes = await page.locator('svg').evaluateAll((els) => 
			els.map((e) => ({ 
				w: e.clientWidth, 
				h: e.clientHeight,
				tagName: e.tagName,
				classes: e.className
			}))
		);
		console.log('SVG boxes found:', boxes);
		
		if (!boxes.some((b) => b.w > 100 && b.h > 100)) {
			throw new Error(`map svg not found with adequate size. Found: ${JSON.stringify(boxes)}`);
		}
	}).toPass({ timeout: 15000 });
});
