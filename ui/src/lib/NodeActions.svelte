<script lang="ts">
import link from '$lib/images/link.svg';
import type { Node, Device } from '$lib/types';
function onUpgrade(i) {
		console.log("upgrade", i);
	}

	export let row: Node;
	export let col;
</script>

{#if row.telemetry?.version}
<button on:click={()=>onUpgrade(row)} class="btn btn-sm variant-filled">
	Upgrade
</button>
{/if}

{#if row.telemetry?.ip}
	<a href="http://{row.telemetry?.ip}" target="_blank" class="btn btn-sm variant-filled">
		<span>Visit</span>
		<span><img class="w-4" src={link} alt="External Link" /></span>
	</a>
{/if}
