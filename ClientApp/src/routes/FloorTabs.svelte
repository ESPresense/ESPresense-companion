<script lang="ts">
  import { getContext, setContext } from 'svelte';
  import { config, devices } from '../lib/stores';
  import { writable } from 'svelte/store';
  import FloorTab from './FloorTab.svelte';
  import Filter from './Filter.svelte';

  export let selected = writable(0);
  setContext('selected', selected);
</script>

<header>
  <nav class="h-50">
    <svg viewBox="0 0 2 3" aria-hidden="true">
        <path d="M0,0 L1,2 C1.5,3 1.5,3 2,3 L2,0 Z" />
    </svg>
    <ul>
  {#if $config?.floors }
  {#each $config?.floors as { name }, value}
    <FloorTab {name} {value} />
  {/each}
  {/if}
  <Filter />
    </ul>
    <svg viewBox="0 0 2 3" aria-hidden="true">
        <path d="M0,0 L0,3 C0.5,3 0.5,3 1,2 L2,0 Z" />
    </svg>
  </nav>
</header>

<style>
  header {
    position: relative;
    display: flex;
    justify-content: space-between;
  }

  nav {
    position: absolute;
    left: 0;
    right: 0;
    top: 0;
    display: flex;
    justify-content: center;
    --background: rgba(255, 255, 255, 0.7);
  }

  svg {
    z-index: 1;
    width: 2em;
    height: 3em;
    display: block;
  }

  path {
    fill: var(--background);
  }

  ul {
    z-index: 1;
    position: relative;
    padding: 0;
    margin: 0;
    height: 3em;
    display: flex;
    justify-content: center;
    align-items: center;
    list-style: none;
    background: var(--background);
    background-size: contain;
  }
</style>