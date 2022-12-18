const config = {
	content: [
		'./src/**/*.{html,js,svelte,ts}',
		require('path').join(require.resolve('@skeletonlabs/skeleton'), '../**/*.{html,js,svelte,ts}')
	],

	darkMode: 'class',

	theme: {
		extend: {}
	},

	plugins: [
		require('@tailwindcss/forms')({
			strategy: 'base', // only generate global styles
		}),
		require('@skeletonlabs/skeleton/tailwind/theme.cjs')
	]
};

module.exports = config;
