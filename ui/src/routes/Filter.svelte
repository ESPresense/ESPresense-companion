<script lang="ts">
  import { showAll } from '../lib/stores';
</script>
<div class="flex items-center">
<input id="showAll" type="checkbox" bind:checked={$showAll} class="input w-4 h-4 text-blue-600 bg-gray-100 rounded border-gray-300 focus:ring-blue-500 dark:focus:ring-blue-600 dark:ring-offset-gray-800 focus:ring-2 dark:bg-gray-700 dark:border-gray-600" />
<label for="showAll" class="ml-1 text-sm font-medium text-gray-900">Show All</label>
</div>