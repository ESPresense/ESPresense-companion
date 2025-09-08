<!--
  Skeleton v3 ProgressBar replacement
-->
<script lang="ts">
  export let value = 0;
  export let max = 100;
  export let height = 'h-2';
  
  $: percentage = Math.min((value / max) * 100, 100);
</script>

<div class="w-full bg-gray-200 rounded-full dark:bg-gray-700 {height}">
  <div
    class="bg-primary-500 {height} rounded-full transition-all duration-300 ease-out"
    style="width: {percentage}%"
  ></div>
</div>