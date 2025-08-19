import { purgeCss } from 'vite-plugin-tailwind-purgecss';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import devtoolsJson from 'vite-plugin-devtools-json';

export default defineConfig({
	plugins: [sveltekit(), purgeCss(), devtoolsJson()],

test: {
include: ['src/**/*.{test,spec,vitest}.{js,ts}']
},

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
