<script>
	import { releases, artifacts } from './firmware';
	export let version = '';
	export let artifact = '';
	export let flavor = '-';
	export let updateMethod = 'release'; // 'self', 'release', 'artifact'
</script>

<div class="mb-4 border rounded p-4">
	<form>
		<div class="flex items-center space-x-4">
			<label for="updateMethod" class="whitespace-nowrap">Update Method:</label>
			<select id="updateMethod" class="flex-grow select" bind:value={updateMethod}>
				<option value="self">Self Update</option>
				<option value="release">Github Releases</option>
				<option value="artifact">Github Artifacts</option>
			</select>
		</div>

		{#if updateMethod != 'self'}
			<div class="flex items-center space-x-2">
				<label for="flavor" class="whitespace-nowrap">Flavor:</label><select id="flavor" class="flex-grow select" bind:value={flavor}><option value="-">(Keep)</option><option value="">Standard</option><option value="verbose">Verbose</option><option value="m5atom">M5Atom</option><option value="m5stickc">M5StickC</option><option value="m5stickc-plus">M5StickC-plus</option><option value="macchina-a0">Macchina A0</option></select>
				{#if updateMethod === 'release'}
					<label for="version" class="whitespace-nowrap">Version:</label>
					<select id="version" class="flex-grow select" bind:value={version}>
						{#each Array.from($releases.entries()).reverse() as [key, value]}
							<optgroup label={key}>
								{#each value as item}
									<option value={item.tag_name}>{item.name}</option>
								{/each}
							</optgroup>
						{/each}
					</select>
				{/if}
				{#if updateMethod === 'artifact'}
					<label for="artifact" class="whitespace-nowrap">Artifact:</label>
					<select id="artifact" class="select" bind:value={artifact}>
						{#each  Array.from($artifacts.entries()).reverse() as [key, value]}
							<optgroup label={key}>
								{#each value as item}
									<option value={item.id}>
										{item.head_sha.substring(0, 7)}: {item.head_commit.message.split('\n')[0]}
									</option>
								{/each}
							</optgroup>
						{/each}
					</select>
				{/if}
			</div>
		{/if}
	</form>
</div>

<style>
	label {
		margin-bottom: 0.2em;
	}
</style>
