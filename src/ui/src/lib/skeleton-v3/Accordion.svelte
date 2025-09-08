<!--
  Skeleton v3 Accordion replacement
-->
<script lang="ts">
  export let duration = 200;
  export let disabled = false;
</script>

<div class="space-y-2" class:opacity-50={disabled}>
  <slot />
</div>