<script lang="ts">
	import { createEventDispatcher } from 'svelte';

	interface Column {
		key: string;
		title: string;
		value?: (row: any) => any;
		sortValue?: (row: any) => any;
		sortable?: boolean;
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
	}

	let { columns, rows, classNameTable = '', sortBy = '' }: Props = $props();

	let sortColumn = $state('');
	let sortDirection = $state<'asc' | 'desc'>('asc');
	
	// Initialize sort from props or defaultSort column
	$effect(() => {
		const defaultColumn = columns.find((c) => c.defaultSort);
		sortColumn = sortBy || defaultColumn?.key || '';
		sortDirection = defaultColumn?.defaultSortDirection || 'asc';
	});

	const dispatch = createEventDispatcher();

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
		if (!sortColumn) return rows;

		const column = columns.find((c) => c.key === sortColumn);
		if (!column) return rows;

		return [...rows].sort((a, b) => {
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

	function handleRowClick(row: any) {
		dispatch('clickRow', { row });
	}

	function getCellValue(row: any, column: Column) {
		return column.value ? column.value(row) : row[column.key];
	}

	let sortedRows = $derived(getSortedRows());
</script>

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
			<tr onclick={() => handleRowClick(row)} class="cursor-pointer">
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
	</tbody>
</table>
