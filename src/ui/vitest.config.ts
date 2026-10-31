import { defineConfig } from 'vitest/config';
import { sveltekit } from '@sveltejs/kit/vite';
import sveltekitConfig from './sveltekit.config.js';

export default defineConfig({
	plugins: [sveltekit(sveltekitConfig)],
	test: {
		include: ['src/**/*.{test,spec,vitest}.{js,ts}'],
		globals: false, // Don't use global expect, import explicitly
		environment: 'jsdom'
	},
	define: {
		// Ensure we're in browser mode for Svelte
		'process.env.NODE_ENV': '"test"'
	}
});
