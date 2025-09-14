import { defineConfig } from 'vitest/config';
import { sveltekit } from '@sveltejs/kit/vite';

export default defineConfig({
	plugins: [sveltekit()],
	test: {
		include: ['src/**/*.{test,spec,vitest}.{js,ts}'],
		globals: false, // Don't use global expect, import explicitly
		environment: 'jsdom',
		setupFiles: ['./src/test/setup.ts']
	},
	define: {
		// Ensure we're in browser mode for Svelte
		'process.env.NODE_ENV': '"test"'
	}
});