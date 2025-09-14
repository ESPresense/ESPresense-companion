import { vi } from 'vitest';

// Setup for Vitest tests

// Ensure browser environment globals for Svelte
Object.assign(globalThis, {
	window: globalThis,
	document: globalThis.document,
	navigator: globalThis.navigator || {},
	location: globalThis.location || {}
});