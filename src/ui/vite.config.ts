import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
	plugins: [
		tailwindcss(),
		sveltekit()
	],

	test: {
		include: ['src/**/*.{test,spec}.{js,ts}']
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
	}
});
