import type { Page } from '@playwright/test';

type MockApiOptions = {
	// If your UI opens a WebSocket on load, stub it to stay fully offline.
	stubWebSocket?: boolean;
};

export async function mockApi(page: Page, options: MockApiOptions = {}) {
	// Demo data used across routes
	const demoConfig = {
		timeout: 30000,
		awayTimeout: 300000,
		floors: [
			{
				id: '1',
				name: 'Ground',
				bounds: [
					[0, 0, 0],
					[100, 100, 0]
				],
				rooms: [
					{
						id: 'room-1',
						name: 'Office',
						points: [
							[10, 10],
							[45, 10],
							[45, 45],
							[10, 45]
						]
					},
					{
						id: 'room-2',
						name: 'Lab',
						points: [
							[55, 10],
							[90, 10],
							[90, 45],
							[55, 45]
						]
					},
					{
						id: 'room-3',
						name: 'Hall',
						points: [
							[10, 55],
							[90, 55],
							[90, 90],
							[10, 90]
						]
					}
				]
			}
		],
		devices: [],
		gps: null,
		map: { flipX: false, flipY: false, wallThickness: 1, wallOpacity: 0.35, wallColor: '#64748b' }
	};

	const demoDevices = [
		{
			id: 'dev-1',
			name: 'Test Device',
			nodes: {},
			room: { id: 'room-1', name: 'Office' },
			floor: { id: '1', name: 'Ground' },
			location: { x: 30, y: 30, z: 1 },
			confidence: 3,
			scale: 1,
			fixes: 1,
			timeout: 30000,
			lastSeen: new Date().toISOString()
		}
	];

	// HTTP API mocks via a single catch-all route so querystrings are covered
	await page.route('**/api/state/**', (route) => {
		const request = route.request();
		const requestUrl = request.url();

		// Be resilient to relative URLs by providing a base.
		let pathname: string;
		try {
			pathname = new URL(requestUrl, 'http://localhost:4173').pathname;
		} catch {
			pathname = requestUrl; // Fallback, still allow includes/endsWith checks
		}

		const respond = (data: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });

		if (pathname.endsWith('/api/state/config')) return respond(demoConfig);
		if (pathname.endsWith('/api/state/devices')) return respond(demoDevices);
		if (pathname.endsWith('/api/state/nodes')) return respond([]);
		if (pathname.endsWith('/api/state/calibration')) return respond({ matrix: {} });
		return respond([]);
	});

	// Other APIs the app may poll occasionally
	await page.route('**/api/devices', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

	// Optionally silence any WebSocket usage in the app
	if (options.stubWebSocket) {
		await page.addInitScript(() => {
			class MockWebSocket {
				readyState = 1; // OPEN
				onopen: ((ev: any) => void) | null = null;
				onmessage: ((ev: any) => void) | null = null;
				onclose: ((ev: any) => void) | null = null;
				onerror: ((ev: any) => void) | null = null;
				constructor() {
					setTimeout(() => this.onopen?.({}), 0);
				}
				send() {}
				close() {
					this.onclose?.({});
				}
				addEventListener(type: string, cb: any) {
					if (type === 'open') this.onopen = cb;
					if (type === 'message') this.onmessage = cb;
					if (type === 'close') this.onclose = cb;
					if (type === 'error') this.onerror = cb;
				}
				removeEventListener() {}
			}
			// @ts-expect-error override in browser context
			window.WebSocket = MockWebSocket;
		});
	}
}
