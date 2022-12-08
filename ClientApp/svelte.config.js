import adapter from '@sveltejs/adapter-static';
import preprocess from 'svelte-preprocess';

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
