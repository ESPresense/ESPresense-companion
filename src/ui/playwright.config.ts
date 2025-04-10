import type { PlaywrightTestConfig } from '@playwright/test';

const config: PlaywrightTestConfig = {
	testDir: './tests',
	testMatch: ['**/*.ts'],
	use: {
		baseURL: 'http://localhost:44490'
	}
};

export default config;
