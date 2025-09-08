<!--
  Skeleton v3 AppRail replacement
  Navigation rail for side navigation
-->
<script lang="ts">
  export let gap = 'gap-1';
  export let regionLead = 'flex-none';
  export let regionDefault = 'flex-1';
  export let regionTrail = 'flex-none';
</script>

<nav class="flex flex-col h-full p-4 {gap}">
  <div class="{regionLead}">
    <slot name="lead" />
  </div>
  
  <div class="{regionDefault} flex flex-col {gap}">
    <slot />
  </div>
  
  <div class="{regionTrail}">
    <slot name="trail" />
  </div>
</nav>