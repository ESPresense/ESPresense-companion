<script lang="ts">
	import { tick } from 'svelte';
	import { apiFetch } from '$lib/api';
	import { gotoSearchResult } from '$lib/urls';
	import type { SearchResult } from '$lib/types';

	let { open = $bindable(false) }: { open?: boolean } = $props();

	let query = $state('');
	let results = $state<SearchResult[]>([]);
	let active = $state(0);
	let loading = $state(false);
	let failed = $state(false);
	let input = $state<HTMLInputElement>();
	let timer: ReturnType<typeof setTimeout> | undefined;
	let seq = 0;

	function onWindowKeydown(event: KeyboardEvent) {
		if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
			event.preventDefault();
			open = !open;
		}
	}

	let opener: Element | null = null;

	$effect(() => {
		if (!open) {
			clearTimeout(timer);
			seq++; // drop any in-flight response
			// Hand focus back to whatever had it, unless navigation already removed it.
			if (opener instanceof HTMLElement && opener.isConnected) opener.focus();
			opener = null;
			return;
		}
		opener = document.activeElement;
		query = '';
		tick().then(() => input?.focus());
	});

	// Re-search whenever the box changes (and once on open, for the empty "recent" list).
	$effect(() => {
		if (!open) return;
		const q = query;
		clearTimeout(timer);
		// Old rows answer the old query: Enter must not open one while the new search is pending.
		seq++;
		results = [];
		active = 0;
		loading = true;
		timer = setTimeout(() => search(q), q ? 120 : 0);
		return () => clearTimeout(timer);
	});

	async function search(q: string) {
		const mine = ++seq;
		loading = true;
		try {
			const found = await apiFetch<SearchResult[]>(`/api/state/search?q=${encodeURIComponent(q)}&limit=20`);
			if (mine !== seq) return;
			results = found;
			active = 0;
			failed = false;
		} catch (e) {
			if (mine !== seq) return;
			console.error('Search failed:', e);
			results = [];
			failed = true;
		} finally {
			if (mine === seq) loading = false;
		}
	}

	function pick(result: SearchResult | undefined) {
		if (!result) return;
		open = false;
		gotoSearchResult(result);
	}

	function onInputKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape') {
			event.preventDefault();
			open = false;
		} else if (event.key === 'Tab') {
			event.preventDefault(); // nothing else in the dialog takes focus; don't let it reach the page behind
		} else if (event.key === 'ArrowDown') {
			event.preventDefault();
			if (results.length) active = (active + 1) % results.length;
		} else if (event.key === 'ArrowUp') {
			event.preventDefault();
			if (results.length) active = (active - 1 + results.length) % results.length;
		} else if (event.key === 'Enter') {
			event.preventDefault();
			pick(results[active]);
		}
	}

	$effect(() => {
		// Keep the highlighted row in view while arrowing through a long list.
		if (open && results.length) document.getElementById(`palette-option-${active}`)?.scrollIntoView({ block: 'nearest' });
	});
</script>

<svelte:window onkeydown={onWindowKeydown} />

{#if open}
	<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
	<div class="fixed inset-0 z-50 flex items-start justify-center bg-black/50 pt-[12vh]" onclick={() => (open = false)}>
		<div class="w-full max-w-xl mx-4 rounded-lg bg-surface-50-950 border border-surface-300-700 shadow-xl overflow-hidden" role="dialog" aria-modal="true" aria-label="Find anything" tabindex="-1" onclick={(e) => e.stopPropagation()}>
			<input bind:this={input} bind:value={query} onkeydown={onInputKeydown} type="text" role="combobox" aria-expanded="true" aria-controls="palette-results" aria-activedescendant={results.length ? `palette-option-${active}` : undefined} aria-label="Find anything" class="input w-full border-0 border-b border-surface-300-700 rounded-none px-4 py-3" placeholder="Find a device or node by name, id, MAC or room…" autocomplete="off" spellcheck="false" />
			<ul id="palette-results" role="listbox" class="max-h-96 overflow-auto">
				{#each results as result, i (`${result.kind}:${result.id}`)}
					<li id="palette-option-{i}" role="option" aria-selected={i === active}>
						<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
						<div class="flex items-center justify-between gap-3 px-4 py-2 cursor-pointer {i === active ? 'bg-primary-500/20' : ''}" onclick={() => pick(result)} onmousemove={() => (active = i)}>
							<div class="min-w-0">
								<div class="truncate font-medium">{result.name || result.id}</div>
								<div class="truncate text-sm text-surface-600-400">
									{#if result.name}<span class="font-mono">{result.id}</span> ·
									{/if}{result.detail}
								</div>
							</div>
							{#if result.badge}
								<span class="shrink-0 rounded px-2 py-0.5 text-xs bg-surface-200-800 text-surface-700-300">{result.badge}</span>
							{/if}
						</div>
					</li>
				{/each}
			</ul>
			{#if failed}
				<p class="px-4 py-3 text-sm text-error-500">Search failed — is the backend running?</p>
			{:else if !loading && results.length === 0}
				<p class="px-4 py-3 text-sm text-surface-600-400">{query.trim() ? 'Nothing matches.' : 'Nothing seen yet.'}</p>
			{/if}
		</div>
	</div>
{/if}
