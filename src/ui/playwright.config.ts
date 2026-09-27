import type { PlaywrightTestConfig } from '@playwright/test';

const config: PlaywrightTestConfig = {
	// The preview server serves a >500kB bundle to every worker at once, so the default
	// 5s assertion timeout can lapse on a loaded machine before the first paint.
	expect: { timeout: 15000 },
	use: {
		baseURL: 'http://localhost:4173',
		launchOptions: {
			args: ['--use-angle=swiftshader']
		}
	},
	webServer: {
		command: 'pnpm run build && pnpm run preview -- --port 4173',
		url: 'http://localhost:4173',
		timeout: 60000,
		reuseExistingServer: !process.env.CI
	},
	testDir: './tests',
	testMatch: '**/*.spec.ts'
};

export default config;
