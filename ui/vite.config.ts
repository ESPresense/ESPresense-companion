import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vitest/config';

export default defineConfig({
	plugins: [sveltekit()],
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
  },
});
