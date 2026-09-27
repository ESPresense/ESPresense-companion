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
		// svelte-eslint-parser needs an inner parser for <script lang="ts">, or every
		// .svelte file fails to parse and is silently never linted.
		files: ['**/*.svelte'],
		languageOptions: {
			parserOptions: { parser: tsEslint.parser }
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
		files: ['**/*.svelte'],
		rules: {
			// Reads `$:` blocks in source order, but Svelte sorts them by dependency: Room.svelte
			// legitimately consumes on line 14 a value assigned on line 20.
			'no-useless-assignment': 'off',
			// A runes-mode rule. Every hit in this (still mostly Svelte 4) codebase is a local
			// cache or a temp inside a `$:` block, where reactivity would be wrong. Revisit
			// with the runes migration in docs/refactor-plan.md.
			'svelte/prefer-svelte-reactivity': 'off',
			// Cannot see through `routes.map((r) => ({ resolved: resolve(r.href) }))`, and flags
			// blob hrefs on download links.
			'svelte/no-navigation-without-resolve': 'off'
		}
	},
	{
		ignores: ['.svelte-kit', 'build', 'package', 'coverage', 'node_modules', 'playwright.config.js']
	}
];
