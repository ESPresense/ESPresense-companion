import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	test: {
		include: ['src/**/*.{test,spec,vitest}.{js,ts}']
	},
	build: { sourcemap: true },
	server: {
		port: 44490,
		proxy: {
			'/api': 'http://localhost:5279',
			'/ws': { target: 'ws://localhost:5279', ws: true }
		},
		host: true
	},
	preview: {
		port: 4173, // Don't proxy API requests in preview mode - let tests handle mocking
		proxy: {}
	}
});
