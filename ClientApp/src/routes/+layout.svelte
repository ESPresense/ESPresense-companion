<script>
	import Header from './Header.svelte';
	import './styles.css';
</script>


<div class="app">
	<Header />

	<main>
		<slot />
	</main>

	<footer>
	</footer>
</div>

