<script lang="ts">
	interface Column {
		key: string;
		title: string;
		value?: (row: any) => any;
		sortValue?: (row: any) => any;
		sortable?: boolean;
		/** Include this column's `value(row)` in the search haystack. Keep it to cheap
		 *  getters — the haystack is built for every row, and the device list can hold
		 *  six figures of rows when "Show All" is on. */
		searchable?: boolean;
		defaultSort?: boolean;
		defaultSortDirection?: 'asc' | 'desc';
		renderComponent?: {
			component: any;
		};
	}

	interface Props {
		columns: Column[];
		rows: any[];
		classNameTable?: string;
		sortBy?: string;
		onclickRow?: (event: { row: any }) => void;
		/** Show a filter box above the table. Always matches `id` and `name`, plus any
		 *  column marked `searchable`. */
		search?: boolean;
		searchPlaceholder?: string;
	}

	let { columns, rows, classNameTable = '', sortBy = '', onclickRow, search = false, searchPlaceholder = 'Filter…' }: Props = $props();

	let query = $state('');

	// Built once per rows/columns change, not per keystroke.
	let haystacks = $derived.by(() => {
		if (!search) return [];
		const extra = columns.filter((c) => c.searchable && c.value);
		return rows.map((r) => {
			let s = `${r.id ?? ''} ${r.name ?? ''}`;
			for (const c of extra) {
				const v = c.value!(r);
				if (v != null) s += ` ${v}`;
			}
			return s.toLowerCase();
		});
	});

	// Space-separated terms are AND-ed, so "kitchen a4c1" narrows.
	let visibleRows = $derived.by(() => {
		const q = query.trim().toLowerCase();
		if (!search || !q) return rows;
		const terms = q.split(/\s+/);
		const out: any[] = [];
		for (let i = 0; i < rows.length; i++) {
			const h = haystacks[i];
			if (h !== undefined && terms.every((t) => h.includes(t))) out.push(rows[i]);
		}
		return out;
	});

	let sortColumn = $state('');
	let sortDirection = $state<'asc' | 'desc'>('asc');

	// Initialize sort from props or defaultSort column
	$effect(() => {
		const defaultColumn = columns.find((c) => c.defaultSort);
		sortColumn = sortBy || defaultColumn?.key || '';
		sortDirection = defaultColumn?.defaultSortDirection || 'asc';
	});

	function handleSort(column: Column) {
		if (!column.sortable) return;

		if (sortColumn === column.key) {
			sortDirection = sortDirection === 'asc' ? 'desc' : 'asc';
		} else {
			sortColumn = column.key;
			sortDirection = 'asc';
		}
	}

	function getSortedRows() {
		if (!sortColumn) return visibleRows;

		const column = columns.find((c) => c.key === sortColumn);
		if (!column) return visibleRows;

		return [...visibleRows].sort((a, b) => {
			let aVal = column.sortValue ? column.sortValue(a) : column.value ? column.value(a) : a[column.key];
			let bVal = column.sortValue ? column.sortValue(b) : column.value ? column.value(b) : b[column.key];

			if (aVal === null || aVal === undefined) aVal = '';
			if (bVal === null || bVal === undefined) bVal = '';

			if (typeof aVal === 'string' && typeof bVal === 'string') {
				aVal = aVal.toLowerCase();
				bVal = bVal.toLowerCase();
			}

			let result = 0;
			if (aVal < bVal) result = -1;
			else if (aVal > bVal) result = 1;

			return sortDirection === 'desc' ? -result : result;
		});
	}

	function isInteractiveTarget(target: EventTarget | null) {
		if (!(target instanceof Element)) return false;

		return target.closest('button, a[href], input, select, textarea, [role="button"], [data-prevent-row-click]') !== null;
	}

	function handleRowClick(event: MouseEvent, row: any) {
		if (event.defaultPrevented) return;

		if (isInteractiveTarget(event.target)) return;

		// A drag that selected text in this row isn't a click on it
		const selection = window.getSelection();
		if (selection?.toString() && selection.containsNode(event.currentTarget as Node, true)) return;

		onclickRow?.({ row });
	}

	function getCellValue(row: any, column: Column) {
		return column.value ? column.value(row) : row[column.key];
	}

	let sortedRows = $derived(getSortedRows());
</script>

{#if search}
	<div class="flex items-center gap-3 mb-2">
		<input type="search" class="input max-w-xs" placeholder={searchPlaceholder} aria-label={searchPlaceholder} bind:value={query} />
		<span class="text-sm text-surface-600-400">
			{#if query.trim()}
				{visibleRows.length} of {rows.length}
			{:else}
				{rows.length}
			{/if}
		</span>
	</div>
{/if}

<table class={classNameTable}>
	<thead>
		<tr>
			{#each columns as column}
				<th class:cursor-pointer={column.sortable} onclick={() => handleSort(column)}>
					{column.title}
					{#if column.sortable && sortColumn === column.key}
						<span class="ml-1">
							{sortDirection === 'asc' ? '↑' : '↓'}
						</span>
					{/if}
				</th>
			{/each}
		</tr>
	</thead>
	<tbody>
		{#each sortedRows as row}
			<tr onclick={(event) => handleRowClick(event, row)} class="cursor-pointer">
				{#each columns as column}
					<td>
						{#if column.renderComponent}
							{@const Component = column.renderComponent.component}
							<Component {row} />
						{:else}
							{getCellValue(row, column) ?? ''}
						{/if}
					</td>
				{/each}
			</tr>
		{/each}
		{#if search && query.trim() && sortedRows.length === 0}
			<tr>
				<td colspan={columns.length} class="py-6 text-center text-surface-600-400">
					Nothing matches “{query.trim()}”.
				</td>
			</tr>
		{/if}
	</tbody>
</table>
