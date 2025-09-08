<!--
  Skeleton v3 RadioItem replacement
-->
<script lang="ts">
  export let group = undefined;
  export let name = '';
  export let value = undefined;
  export let checked = false;
  
  function handleChange(e) {
    group = value;
  }
  
  $: checked = group === value;
</script>

<label class="flex items-center cursor-pointer">
  <input
    type="radio"
    bind:group
    {name}
    {value}
    on:change={handleChange}
    class="w-4 h-4 text-primary-500 border-gray-300 focus:ring-primary-500 dark:focus:ring-primary-600 dark:ring-offset-gray-800 focus:ring-2 dark:bg-gray-700 dark:border-gray-600"
  />
  <span class="ml-2 text-sm font-medium text-gray-900 dark:text-gray-300">
    <slot />
  </span>
</label>