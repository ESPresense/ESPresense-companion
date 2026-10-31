import adapter from '@sveltejs/adapter-static';
import * as child_process from 'node:child_process';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';
import devtoolsJson from 'vite-plugin-devtools-json';

export default defineConfig({
	plugins: [
		devtoolsJson(),
		tailwindcss(),
		sveltekit({
			extensions: ['.svelte'],
			preprocess: [vitePreprocess({ script: true })],
			inspector: true,
			onwarn: (warning, handler) => {
				// Suppress a11y warnings
				if (warning.code?.startsWith('a11y')) return;
				handler(warning);
			},
			embedded: true,
			inlineStyleThreshold: 4096,
			prerender: { crawl: false },
			adapter: adapter({ strict: false }),
			version: {
				name: child_process.execSync('git rev-parse HEAD').toString().trim(),
				pollInterval: 5000
			}
		})
	],

	build: {
		sourcemap: true
	},

	server: {
		port: 44490,
		proxy: {
			'/api': 'http://localhost:5279',
			'/ws': {
				target: 'ws://localhost:5279',
				ws: true
			}
		},
		host: true
	},

	preview: {
		port: 4173,
		// Don't proxy API requests in preview mode - let tests handle mocking
		proxy: {}
	}
});
