<!--
  Skeleton v3 AppRailAnchor replacement
  Navigation anchor for app rail items
-->
<script lang="ts">
  export let href = '';
  export let target = '_self';
  export let selected = false;
  export let name = '';
  export let group = '';
  
  $: classes = `flex flex-col items-center p-2 rounded-md transition-colors ${
    selected 
      ? 'bg-primary-500 text-white' 
      : 'hover:bg-surface-200 text-surface-700 dark:hover:bg-surface-700 dark:text-surface-300'
  }`;
</script>

<a {href} {target} class={classes} data-name={name} data-group={group}>
  <slot />
</a>