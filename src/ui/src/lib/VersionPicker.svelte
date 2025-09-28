<script>
	import { releases, artifacts } from './firmware';
	export let version = '';
	export let artifact = '';
	export let flavor = '-';
	export let updateMethod = 'self'; // 'self', 'manual', 'recovery'
	export let firmwareSource = 'release'; // 'release', 'artifact'
</script>

<div class="card mb-6 space-y-6 border border-surface-300-700 bg-surface-50-950 p-6 shadow-lg rounded-lg">
	<form class="space-y-6" aria-label="Firmware update preferences">
		<div class="flex flex-wrap items-start justify-between gap-6">
			<div class="flex-1 min-w-0">
				<h3 class="text-lg font-semibold text-surface-800-100 mb-2">Firmware Update</h3>
				<p class="text-sm text-surface-600-400 leading-relaxed">Choose how nodes receive firmware changes.</p>
			</div>
			<div class="flex-shrink-0 w-full sm:w-auto sm:min-w-[280px]">
				<label class="block" for="updateMethod">
					<span class="block text-sm font-medium text-surface-700-300 mb-2">Update Method</span>
					<select id="updateMethod" class="select w-full bg-surface-100-900 border-surface-300-600 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 rounded-md" bind:value={updateMethod}>
						<option value="self">🔄 Auto - device selects the latest firmware</option>
						<option value="manual">⚙️ Manual - choose a specific version</option>
						<option value="recovery">🔧 Recovery - upload via ESP OTA</option>
					</select>
				</label>
			</div>
		</div>

		{#if updateMethod != 'self'}
			<div class="border-t border-surface-300-700 pt-6">
				<div class="grid gap-6 sm:grid-cols-1 md:grid-cols-2 lg:grid-cols-3">
					<label class="block" for="flavor">
						<span class="block text-sm font-medium text-surface-700-300 mb-2">Flavor</span>
						<select id="flavor" class="select w-full bg-surface-100-900 border-surface-300-600 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 rounded-md" bind:value={flavor}>
							<option value="-">🔒 (Keep Current)</option>
							<option value="">📦 Standard</option>
							<option value="cdc">🔌 CDC (USB Serial)</option>
							<option value="verbose">🔍 Verbose</option>
							<option value="m5atom">🟢 M5Atom</option>
							<option value="m5stickc">🔵 M5StickC</option>
							<option value="m5stickc-plus">🔵 M5StickC Plus</option>
							<option value="macchina-a0">🚗 Macchina A0</option>
						</select>
					</label>

					<label class="block" for="source">
						<span class="block text-sm font-medium text-surface-700-300 mb-2">Source</span>
						<select id="source" class="select w-full bg-surface-100-900 border-surface-300-600 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 rounded-md" bind:value={firmwareSource}>
							<option value="release">🏷️ GitHub Releases</option>
							<option value="artifact">🔨 GitHub Artifacts</option>
						</select>
					</label>

					{#if firmwareSource === 'release'}
						<label class="block" for="version">
							<span class="block text-sm font-medium text-surface-700-300 mb-2">Version</span>
							{#if $releases.size === 0}
								<div class="flex items-center justify-center h-10 bg-surface-100-800 border border-surface-300-600 rounded-md">
									<span class="text-sm text-surface-500-400 flex items-center gap-2">
										<svg class="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
											<circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
											<path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
										</svg>
										Loading releases...
									</span>
								</div>
							{:else}
								<select id="version" class="select w-full bg-surface-100-900 border-surface-300-600 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 rounded-md" bind:value={version}>
									{#each Array.from($releases.entries()).reverse() as [key, value]}
										<optgroup label={key}>
											{#each value as item}
												<option value={item.tag_name}>{item.name}</option>
											{/each}
										</optgroup>
									{/each}
								</select>
							{/if}
						</label>
					{/if}

					{#if firmwareSource === 'artifact'}
						<label class="block" for="artifact">
							<span class="block text-sm font-medium text-surface-700-300 mb-2">Artifact</span>
							{#if $artifacts.size === 0}
								<div class="flex items-center justify-center h-10 bg-surface-100-800 border border-surface-300-600 rounded-md">
									<span class="text-sm text-surface-500-400 flex items-center gap-2">
										<svg class="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
											<circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
											<path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
										</svg>
										Loading artifacts...
									</span>
								</div>
							{:else}
								<select id="artifact" class="select w-full bg-surface-100-900 border-surface-300-600 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 rounded-md" bind:value={artifact}>
									{#each Array.from($artifacts.entries()).reverse() as [key, value]}
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
						</label>
					{/if}
				</div>
			</div>
		{/if}
	</form>
</div>
