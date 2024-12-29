import adapter from '@sveltejs/adapter-static';
import * as child_process from 'node:child_process';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	extensions: ['.svelte'],

	preprocess: [vitePreprocess()],

	relative: true,

	vitePlugin: {
		inspector: true
	},

	kit: {
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
	}
};

export default config;
