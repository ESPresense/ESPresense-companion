import type { PlaywrightTestConfig } from '@playwright/test';

const config: PlaywrightTestConfig = {
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
