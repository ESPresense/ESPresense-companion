import { expect, test } from '@playwright/test';
import { mockApi } from './mock-api';

test('recovery update allows Standard firmware', async ({ page }) => {
	const firmwareManifest = {
		firmware: [
			{ name: 'esp32-standard.bin', cpu: 'esp32', flavor: '' },
			{ name: 'esp32c3-cdc.bin', cpu: 'esp32c3', flavor: 'cdc' }
		],
		flavors: [
			{ name: 'Standard', value: '', cpus: ['esp32'] },
			{ name: 'CDC', value: 'cdc', cpus: ['esp32c3'] }
		],
		cpus: [
			{ name: 'ESP32', value: 'esp32' },
			{ name: 'ESP32-C3', value: 'esp32c3' }
		]
	};

	const releases = [
		{
			assets: Array.from({ length: 7 }, (_, index) => ({ id: index + 1, name: `asset-${index}` })),
			prerelease: false,
			tag_name: 'v1.0.0',
			name: 'v1.0.0'
		}
	];

	const nodes = [
		{
			id: 'node-1',
			name: 'Recovery Node',
			telemetry: { version: '1.0.0', ip: '192.168.1.5' },
			cpu: { value: 'esp32', name: 'ESP32' },
			flavor: { value: '', name: 'Standard', cpus: ['esp32'] },
			online: true,
			location: { x: 0, y: 0, z: 0 },
			floors: [],
			nodes: {},
			sourceType: 'Config'
		}
	];

	await mockApi(page, {
		stubWebSocket: true,
		nodes,
		firmwareTypes: firmwareManifest,
		releases
	});

	await page.goto('/nodes');

	await expect(page.getByRole('heading', { name: 'Nodes' })).toBeVisible();
	await expect(page.getByText('Recovery Node')).toBeVisible();

	await page.locator('#updateMethod').selectOption('recovery');
	await page.locator('#flavor').selectOption({ value: '' });
	await page.locator('#version').selectOption('v1.0.0');

	await page.getByRole('button', { name: 'Update node firmware' }).click();

	const firmwareModal = page.getByRole('dialog');
	await expect(firmwareModal).toBeVisible();

	await firmwareModal.getByLabel('Firmware').selectOption('esp32-standard.bin');
	const modalUpdateButton = firmwareModal.getByRole('button', { name: 'Update' });
	await expect(modalUpdateButton).toBeEnabled();
});
