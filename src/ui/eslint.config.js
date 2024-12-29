import js from '@eslint/js';
import eslintConfigPrettier from 'eslint-config-prettier';
import eslintPluginSvelte from 'eslint-plugin-svelte';
import globals from 'globals';
import tsEslint from 'typescript-eslint';
import vitest from 'eslint-plugin-vitest';
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
	vitest.configs.recommended,
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
		ignores: ['.svelte-kit', 'build', 'package', 'coverage', 'node_modules', 'playwright.config.js']
	}
];
