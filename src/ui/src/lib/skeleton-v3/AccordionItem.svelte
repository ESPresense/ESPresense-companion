<!--
  Skeleton v3 AccordionItem replacement
-->
<script lang="ts">
  import { slide } from 'svelte/transition';
  
  export let open = false;
  export let disabled = false;
  
  function toggle() {
    if (!disabled) {
      open = !open;
    }
  }
</script>

<div class="border border-gray-200 dark:border-gray-700 rounded-lg">
  <button
    class="w-full px-4 py-3 text-left font-medium bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 dark:hover:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-primary-500 disabled:opacity-50 disabled:cursor-not-allowed"
    on:click={toggle}
    {disabled}
  >
    <div class="flex justify-between items-center">
      <slot name="summary" />
      <svg
        class="w-5 h-5 transition-transform duration-200 {open ? 'rotate-180' : ''}"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
      </svg>
    </div>
  </button>
  
  {#if open}
    <div class="px-4 py-3 border-t border-gray-200 dark:border-gray-700" transition:slide>
      <slot />
    </div>
  {/if}
</div>