<script lang="ts">
	import { tick } from 'svelte';
	import { apiFetch } from '$lib/api';
	import { gotoDetail, gotoNodeById } from '$lib/urls';
	import type { Device, DeviceSearchResult, Node } from '$lib/types';

	type Row = { kind: 'node'; key: string; node: Node } | { kind: 'device'; key: string; device: DeviceSearchResult };

	let { open = $bindable(false) }: { open?: boolean } = $props();

	let query = $state('');
	let deviceResults = $state<DeviceSearchResult[]>([]);
	// Nodes are few, so fetch them once per open and filter in the browser.
	let allNodes = $state<Node[]>([]);
	const MAX_NODES = 5;

	const nodeResults = $derived.by(() => {
		const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
		if (!terms.length) return [];
		return allNodes.filter((n) => terms.every((t) => n.id.toLowerCase().includes(t) || (n.name ?? '').toLowerCase().includes(t))).slice(0, MAX_NODES);
	});

	const results = $derived<Row[]>([...nodeResults.map((node): Row => ({ kind: 'node', key: `node:${node.id}`, node })), ...deviceResults.map((device): Row => ({ kind: 'device', key: `device:${device.id}`, device }))]);
	let active = $state(0);
	let loading = $state(false);
	let failed = $state(false);
	let input = $state<HTMLInputElement>();
	let timer: ReturnType<typeof setTimeout> | undefined;
	let seq = 0;
	let nodesSeq = 0;

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
			nodesSeq++;
			// Hand focus back to whatever had it, unless navigation already removed it.
			if (opener instanceof HTMLElement && opener.isConnected) opener.focus();
			opener = null;
			return;
		}
		opener = document.activeElement;
		query = '';
		const mineNodes = ++nodesSeq;
		apiFetch<Node[]>('/api/state/nodes?includeTele=false')
			.then((n) => {
				if (mineNodes === nodesSeq) allNodes = n;
			})
			.catch((e) => console.error('Node list failed:', e));
		tick().then(() => input?.focus());
	});

	// Re-search whenever the box changes (and once on open, for the empty "recent" list).
	$effect(() => {
		if (!open) return;
		const q = query;
		clearTimeout(timer);
		// Old rows answer the old query: Enter must not open one while the new search is pending.
		seq++;
		deviceResults = [];
		active = 0;
		loading = true;
		timer = setTimeout(() => search(q), q ? 120 : 0);
		return () => clearTimeout(timer);
	});

	async function search(q: string) {
		const mine = ++seq;
		loading = true;
		try {
			const found = await apiFetch<DeviceSearchResult[]>(`/api/state/devices/search?q=${encodeURIComponent(q)}&limit=20`);
			if (mine !== seq) return;
			deviceResults = found;
			active = 0;
			failed = false;
		} catch (e) {
			if (mine !== seq) return;
			console.error('Device search failed:', e);
			deviceResults = [];
			failed = true;
		} finally {
			if (mine === seq) loading = false;
		}
	}

	function pick(row: Row | undefined) {
		if (!row) return;
		open = false;
		if (row.kind === 'node') gotoNodeById(row.node.id);
		else gotoDetail({ id: row.device.id } as Device);
	}

	function onInputKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape') {
			event.preventDefault();
			open = false;
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
		if (active >= results.length) active = 0;
	});

	$effect(() => {
		// Keep the highlighted row in view while arrowing through a long list.
		if (open && results.length) document.getElementById(`palette-option-${active}`)?.scrollIntoView({ block: 'nearest' });
	});

	function detail(r: DeviceSearchResult): string {
		const parts: string[] = [];
		if (r.room) parts.push(r.room);
		if (r.nodes > 0) parts.push(r.nearestNode ? `${r.nodes} nodes · nearest ${r.nearestNode} ${r.nearestDistance?.toFixed(2)} m` : `${r.nodes} nodes`);
		else parts.push('not heard recently');
		return parts.join(' · ');
	}
</script>

<svelte:window onkeydown={onWindowKeydown} />

{#if open}
	<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
	<div class="fixed inset-0 z-50 flex items-start justify-center bg-black/50 pt-[12vh]" onclick={() => (open = false)}>
		<div class="w-full max-w-xl mx-4 rounded-lg bg-surface-50-950 border border-surface-300-700 shadow-xl overflow-hidden" role="dialog" aria-modal="true" aria-label="Find a device" tabindex="-1" onclick={(e) => e.stopPropagation()}>
			<input bind:this={input} bind:value={query} onkeydown={onInputKeydown} type="text" role="combobox" aria-expanded="true" aria-controls="palette-results" aria-activedescendant={results.length ? `palette-option-${active}` : undefined} aria-label="Find a device" class="input w-full border-0 border-b border-surface-300-700 rounded-none px-4 py-3" placeholder="Find a device or node by name, id, MAC or room…" autocomplete="off" spellcheck="false" />
			<ul id="palette-results" role="listbox" class="max-h-96 overflow-auto">
				{#each results as row, i (row.key)}
					<li id="palette-option-{i}" role="option" aria-selected={i === active}>
						<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
						<div class="flex items-center justify-between gap-3 px-4 py-2 cursor-pointer {i === active ? 'bg-primary-500/20' : ''}" onclick={() => pick(row)} onmousemove={() => (active = i)}>
							{#if row.kind === 'node'}
								<div class="min-w-0">
									<div class="truncate font-medium">{row.node.name || row.node.id}</div>
									<div class="truncate text-sm text-surface-600-400">
										{#if row.node.name}<span class="font-mono">{row.node.id}</span> ·
										{/if}{row.node.online ? 'online' : 'offline'}
									</div>
								</div>
								<span class="shrink-0 rounded px-2 py-0.5 text-xs bg-primary-500/20 text-primary-700-300">node</span>
							{:else}
								<div class="min-w-0">
									<div class="truncate font-medium">{row.device.name || row.device.id}</div>
									<div class="truncate text-sm text-surface-600-400">
										{#if row.device.name}<span class="font-mono">{row.device.id}</span> ·
										{/if}{detail(row.device)}
									</div>
								</div>
								{#if !row.device.tracked}
									<span class="shrink-0 rounded px-2 py-0.5 text-xs bg-surface-200-800 text-surface-700-300">untracked</span>
								{/if}
							{/if}
						</div>
					</li>
				{/each}
			</ul>
			{#if failed}
				<p class="px-4 py-3 text-sm text-error-500">Search failed — is the backend running?</p>
			{:else if !loading && results.length === 0}
				<p class="px-4 py-3 text-sm text-surface-600-400">{query.trim() ? 'Nothing matches.' : 'No devices seen yet.'}</p>
			{/if}
		</div>
	</div>
{/if}
