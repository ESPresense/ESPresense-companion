<script lang="ts">
	import { config } from '$lib/stores';
	import type { Node } from '$lib/types';

	let nodes: Node[] | undefined;
	$: nodes = $config?.nodes;
</script>

<div class="table-container">
	{#if nodes}
		<table class="table table-hover">
			<thead>
				<tr>
					<th>Name</th>
				</tr>
			</thead>
			<tbody>
				{#each nodes as n}
					<tr>
						<td>{@html n.name}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>

<style>
  .table-container { padding: 10px; }
</style>
