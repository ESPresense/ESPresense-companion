<script lang="ts">
	import { apiFetch } from '#lib/api.js';
	import { config } from '#lib/stores.js';
	import type { DeviceHistory } from '#lib/types.js';
	import { advance, formatClock, isOtherDay, mergeFixes, retentionMs, sampleEverySeconds, toFixes, viewAt, type HistoryFix, type HistoryView } from '#lib/history.js';

	interface Props {
		deviceId: string;
		/** The history at the moment shown, whenever it changes; null while history isn't being recorded. */
		onview?: ((view: HistoryView | null) => void) | undefined;
	}

	let { deviceId, onview = undefined }: Props = $props();

	const speeds = [1, 10, 60];
	const trailOptions = [5, 15, 60];

	let fixes: HistoryFix[] = $state.raw([]);
	let now = $state(Date.now());
	let live = $state(true);
	let cursor = $state(Date.now());
	let playing = $state(false);
	let speed = $state(10);
	let trailMinutes = $state(15);
	let loading = $state(false);
	let error: string | null = $state(null);

	let enabled = $derived($config?.history?.enabled ?? false);
	let retention = $derived(retentionMs($config));
	let rangeStart = $derived(fixes.length ? fixes[0].t : now - retention);
	let shownAt = $derived(live ? now : Math.min(Math.max(cursor, rangeStart), now));
	let view = $derived(enabled ? viewAt($config, fixes, shownAt, trailMinutes * 60_000, live) : null);

	let timeLabel = $derived(formatClock(shownAt) + (isOtherDay(shownAt, now) ? ` ${new Date(shownAt).toLocaleDateString()}` : ''));
	let whereLabel = $derived(!view ? '' : view.current ? `${view.room?.name ?? 'Outside rooms'}${view.floor ? ` · ${view.floor.name}` : ''}` : fixes.length ? 'No fix' : loading ? 'Loading…' : 'No history yet');

	$effect(() => {
		onview?.(view);
	});

	$effect(() => {
		const timer = setInterval(() => (now = Date.now()), 1000);
		return () => clearInterval(timer);
	});

	async function fetchFixes(id: string, since: number, until: number, every: number): Promise<HistoryFix[]> {
		const query = `start=${new Date(since).toISOString()}&end=${new Date(until).toISOString()}&best=true&every=${every}`;
		const data = await apiFetch<{ history: DeviceHistory[] }>(`/api/history/${encodeURIComponent(id)}/range?${query}`);
		return toFixes(data?.history ?? []);
	}

	// The whole retained window once, thinned to a few thousand points; after that only what is new, every few seconds.
	$effect(() => {
		if (!enabled || !deviceId) return;
		const id = deviceId;
		const keep = retention;
		let cancelled = false;
		let fetchedTo = 0;
		fixes = [];
		loading = true;
		error = null;

		async function poll() {
			const t = Date.now();
			const initial = fetchedTo === 0;
			try {
				const got = await fetchFixes(id, initial ? t - keep : fetchedTo - 1000, t + 60_000, initial ? sampleEverySeconds(keep) : 1);
				if (cancelled) return;
				fixes = mergeFixes(fixes, got, t - keep);
				fetchedTo = t;
				error = null;
			} catch (e) {
				if (!cancelled) error = e instanceof Error ? e.message : String(e);
			} finally {
				if (!cancelled) loading = false;
			}
		}

		poll();
		const timer = setInterval(poll, 5000);
		return () => {
			cancelled = true;
			clearInterval(timer);
		};
	});

	// Playback: wall time × speed, until it reaches the present and hands back to live.
	$effect(() => {
		if (!playing) return;
		let last = performance.now();
		const timer = setInterval(() => {
			const t = performance.now();
			const step = advance(cursor, t - last, speed, Date.now());
			last = t;
			cursor = step.cursor;
			if (step.atEnd) {
				playing = false;
				live = true;
			}
		}, 100);
		return () => clearInterval(timer);
	});

	function togglePlay() {
		if (live) {
			// Nothing lies ahead of the present, so play replays the trail window.
			cursor = Math.max(rangeStart, now - trailMinutes * 60_000);
			live = false;
			playing = true;
		} else playing = !playing;
	}

	function goLive() {
		playing = false;
		live = true;
	}

	function scrub(value: number) {
		playing = false;
		if (value >= now - 1500) {
			live = true;
			return;
		}
		live = false;
		cursor = value;
	}
</script>

{#if !enabled}
	<div class="flex flex-wrap items-center gap-2 border-t border-surface-300-700 bg-surface-100-900 px-4 py-2 text-sm" data-testid="device-history">
		<span class="opacity-70">History isn't being recorded.</span>
		<span>Set <code class="code">history.enabled: true</code> in config.yaml to scrub this device's past positions.</span>
		<a class="anchor" href="https://espresense.com/companion" target="_blank" rel="noreferrer">Docs</a>
	</div>
{:else}
	<div class="flex flex-wrap items-center gap-3 border-t border-surface-300-700 bg-surface-100-900 px-4 py-2 text-sm" data-testid="device-history">
		<button type="button" class="btn btn-sm {live ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={goLive} aria-pressed={live} title="Follow the present">Live</button>
		<button type="button" class="btn btn-sm preset-tonal w-10" onclick={togglePlay} aria-label={playing ? 'Pause' : 'Play'} title={playing ? 'Pause' : live ? 'Replay the trail' : 'Play'}>{playing ? '❚❚' : '▶'}</button>
		<select class="select w-20 py-0.5" bind:value={speed} aria-label="Playback speed">
			{#each speeds as s}
				<option value={s}>{s}×</option>
			{/each}
		</select>
		<input class="min-w-40 flex-1" type="range" min={rangeStart} max={now} step="1000" value={shownAt} oninput={(e) => scrub(Number(e.currentTarget.value))} aria-label="Moment shown" data-testid="history-slider" />
		<span class="font-mono tabular-nums" data-testid="history-time">{timeLabel}</span>
		<span class="min-w-32" data-testid="history-where">{whereLabel}</span>
		<label class="flex items-center gap-1">
			<span class="opacity-70">Trail</span>
			<select class="select w-24 py-0.5" bind:value={trailMinutes} aria-label="Trail length">
				{#each trailOptions as m}
					<option value={m}>{m} min</option>
				{/each}
			</select>
		</label>
		{#if error}
			<span class="text-error-500" data-testid="history-error">{error}</span>
		{/if}
	</div>
{/if}
