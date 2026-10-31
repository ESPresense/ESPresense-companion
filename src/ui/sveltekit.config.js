import adapter from '@sveltejs/adapter-static';
import * as child_process from 'node:child_process';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/**
 * Options for the sveltekit() plugin, shared by vite.config.ts and vitest.config.ts.
 * @type {import('@sveltejs/kit/vite').Config}
 */
const config = {
	extensions: ['.svelte'],

	preprocess: [vitePreprocess({ script: true })],

	inspector: true,

	onwarn: (warning, handler) => {
		// Suppress a11y warnings
		if (warning.code?.startsWith('a11y')) return;
		handler(warning);
	},

	// Kit 3 replaces $lib with #lib; keep $lib so open branches don't all conflict.
	alias: { $lib: 'src/lib' },

	embedded: true,
	inlineStyleThreshold: 4096,
	prerender: {
		crawl: false
	},
	adapter: adapter({
		strict: false
	}),
	version: {
		name: child_process.execSync('git rev-parse HEAD').toString().trim(),
		pollInterval: 5000
	}
};

export default config;
