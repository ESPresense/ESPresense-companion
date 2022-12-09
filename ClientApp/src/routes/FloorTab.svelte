<script lang="ts">
  import { getContext } from 'svelte';

  const selected = getContext('selected')
  export let name = '1st Floor';
  export let value = 1;

  const onClickHandler = (event) => {
    event.preventDefault();
    selected.set(value);
  }
</script>


<li>
  <a href="#" on:click={onClickHandler} class:first={value==0}>{name}</a>
</li>

<style>

	li {
		position: relative;
		height: 100%;
	}

	li::before {
		--size: 6px;
		content: '';
		width: 0;
		height: 0;
		position: absolute;
		top: 0;
		left: calc(50% - var(--size));
		border: var(--size) solid transparent;
		border-top: var(--size) solid var(--color-theme-1);
	}

	a {
		display: flex;
		height: 100%;
		align-items: center;
		padding: 0 0.5rem;
		color: var(--color-text);
		font-weight: 700;
		font-size: 0.8rem;
		text-transform: uppercase;
		letter-spacing: 0.1em;
		text-decoration: none;
		transition: color 0.2s linear;
	}

	a:hover {
		color: var(--color-theme-1);
	}
</style>