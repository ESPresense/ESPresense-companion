import type { Config } from 'tailwindcss';
import forms from '@tailwindcss/forms';

const config = {
	darkMode: 'class',
	content: [
		'./src/**/*.{html,js,svelte,ts}'
	],
	theme: {
		extend: {
			colors: {
				primary: {
					500: '#3b82f6'
				},
				surface: {
					100: '#f5f5f5',
					200: '#e5e5e5',
					300: '#d4d4d4',
					700: '#374151',
					900: '#111827'
				}
			}
		}
	},
	plugins: [forms]
} satisfies Config;

export default config;
