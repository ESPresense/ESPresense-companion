<!--
  Skeleton v3 SlideToggle replacement
-->
<script lang="ts">
  export let checked = false;
  export let disabled = false;
  export let name = '';
  export let value = '';
  
  function handleChange(e) {
    checked = e.target.checked;
  }
</script>

<label class="flex items-center cursor-pointer" class:opacity-50={disabled}>
  <div class="relative">
    <input
      type="checkbox"
      class="sr-only"
      {checked}
      {disabled}
      {name}
      {value}
      on:change={handleChange}
    />
    <div
      class="w-10 h-6 bg-gray-200 rounded-full shadow-inner transition-colors duration-200 {checked ? 'bg-primary-500' : ''}"
    ></div>
    <div
      class="absolute w-4 h-4 bg-white rounded-full shadow -top-1 transition-transform duration-200 {checked ? 'translate-x-5' : 'translate-x-1'}"
    ></div>
  </div>
  <span class="ml-3 text-sm font-medium text-gray-900 dark:text-gray-300">
    <slot />
  </span>
</label>