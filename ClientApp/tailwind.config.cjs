const config = {
	content: [
		require('path').join(require.resolve('@skeletonlabs/skeleton'), '../**/*.{html,js,svelte,ts}')
	],

	darkMode: 'class',

	theme: {
		extend: {}
	},

    plugins: [
        require('@skeletonlabs/skeleton/tailwind/theme.cjs')
    ]
};

module.exports = config;
