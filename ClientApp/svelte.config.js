import adapter from '@sveltejs/adapter-static';
import preprocess from 'svelte-preprocess';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	preprocess: [
		preprocess({
			postcss: true
		})
	],

	kit: {
		prerender: {
			crawl: false
		},
		adapter: adapter({
			fallback: 'index.html',
			strict: false
		})
	}
};

export default config;
