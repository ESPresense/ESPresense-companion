import js from '@eslint/js';
import eslintConfigPrettier from 'eslint-config-prettier';
import eslintPluginSvelte from 'eslint-plugin-svelte';
import globals from 'globals';
import tsEslint from 'typescript-eslint';
import playwright from 'eslint-plugin-playwright';

export default [
	js.configs.recommended,
	...tsEslint.configs.recommended,
	...eslintPluginSvelte.configs['flat/recommended'],
	eslintConfigPrettier,
	{
		...playwright.configs['flat/playwright'],
		files: ['tests/**']
	},
	...eslintPluginSvelte.configs['flat/prettier'],
	{
		languageOptions: {
			ecmaVersion: 'latest',
			sourceType: 'module',
			globals: { ...globals.node, ...globals.browser },
			parserOptions: {
				extraFileExtensions: ['.svelte']
			}
		}
	},
	{
		rules: {
			'@typescript-eslint/no-explicit-any': 'off',
			'@typescript-eslint/no-unused-vars': 'off',
			'svelte/require-each-key': 'off',
			'prefer-const': 'off',
			'no-var': 'off'
		}
	},
	{
		ignores: ['.svelte-kit', 'build', 'package', 'coverage', 'node_modules', 'playwright.config.js']
	}
];
