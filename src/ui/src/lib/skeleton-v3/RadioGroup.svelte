<!--
  Skeleton v3 RadioGroup replacement
-->
<script lang="ts">
  export let selected = undefined;
  export let name = '';
  export let gap = 'gap-2';
  export let flexDirection = 'flex-col';
</script>

<div class="flex {flexDirection} {gap}">
  <slot {selected} />
</div>