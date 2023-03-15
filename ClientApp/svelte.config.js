import adapter from '@sveltejs/adapter-static';
import preprocess from 'svelte-preprocess';
import * as child_process from 'node:child_process';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	compilerOptions: {
		enableSourcemap: true,
  },

	preprocess: [
		preprocess({
			sourceMap: true,
			postcss: true
		})
	],

  kit: {
    embedded: true,
    inlineStyleThreshold: 4096,
		prerender: {
			crawl: false
		},
		adapter: adapter({
			fallback: 'index.html',
			strict: false
    }),
    version: {
      name: child_process.execSync('git rev-parse HEAD').toString().trim(),
      pollInterval: 5000
    },
	}
};

export default config;
