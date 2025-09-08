<!--
  Skeleton v3 AppShell replacement
  Minimal component that replicates the AppShell layout structure
-->
<script lang="ts">
  export let regionSidebar = 'w-auto';
  export let regionPage = 'relative flex-1';
</script>

<div class="relative flex h-full">
  <!-- Sidebar -->
  <aside class="{regionSidebar}">
    <slot name="sidebarLeft" />
  </aside>
  
  <!-- Main Content -->
  <main class="{regionPage}">
    <slot />
  </main>
  
  <!-- Right Sidebar -->
  <aside class="w-auto">
    <slot name="sidebarRight" />
  </aside>
</div>