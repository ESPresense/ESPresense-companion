import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';
import devtoolsJson from 'vite-plugin-devtools-json';

export default defineConfig({
	plugins: [devtoolsJson(), tailwindcss(), sveltekit()],


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
