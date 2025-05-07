<script lang="ts">
	import type { NodeSetting } from './types';

	export let settings: NodeSetting; // Assume parent handles loading and provides this
</script>

{#if settings}
	<div class="card p-4 max-h-[90vh] overflow-auto">
		<!-- Two Column Layout -->
		<div class="grid grid-cols-1 md:grid-cols-2 gap-4">
			<!-- Left Column -->
			<div>
				<!-- Basic Information -->
				<h3 class="h3 mb-2">Basic Information</h3>
				<div class="grid grid-cols-2 gap-2 mb-4">
					<label class="label">
						<span>ID</span>
						<input class="input" type="text" disabled bind:value={settings.id} />
					</label>
					<label class="label">
						<span>Name</span>
						<input class="input" type="text" bind:value={settings.name} />
					</label>
				</div>

				<!-- Calibration -->
				<h3 class="h3 mb-2">Calibration</h3>
				<div class="grid grid-cols-2 gap-2 mb-4">
					<label class="label">
						<span>RSSI at 1m</span>
						<input class="input" type="number" bind:value={settings.calibration.rxRefRssi} />
					</label>
					<label class="label">
						<span>Rx Adj RSSI</span>
						<input class="input" type="number" bind:value={settings.calibration.rxAdjRssi} />
					</label>
					<label class="label">
						<span>Absorption</span>
						<input class="input" type="number" step="0.01" bind:value={settings.calibration.absorption} />
					</label>
					<label class="label">
						<span>Tx Ref RSSI</span>
						<input class="input" type="number" bind:value={settings.calibration.txRefRssi} />
					</label>
				</div>
			</div>

			<!-- Right Column -->
			<div>
				<!-- Filtering -->
				<h3 class="h3 mb-2">Filtering</h3>
				<div class="grid gap-2 mb-4">
					<label class="label">
						<span>Include IDs</span>
						<input class="input" type="text" bind:value={settings.filtering.includeIds} />
					</label>
					<label class="label">
						<span>Exclude IDs</span>
						<input class="input" type="text" bind:value={settings.filtering.excludeIds} />
					</label>
					<label class="label">
						<span>Max Distance (m)</span>
						<input class="input" type="number" step="0.01" min="0" bind:value={settings.filtering.maxDistance} />
					</label>
				</div>

				<!-- Updating -->
				<h3 class="h3 mb-2">Updating</h3>
				<div class="grid grid-cols-2 gap-2 mb-4">
					<label class="flex items-center space-x-2">
						<input class="checkbox" type="checkbox" bind:checked={settings.updating.autoUpdate} />
						<span>Auto Update</span>
					</label>
					<label class="flex items-center space-x-2">
						<input class="checkbox" type="checkbox" bind:checked={settings.updating.prerelease} />
						<span>Include Pre-releases</span>
					</label>
				</div>
			</div>
		</div>

		<!-- Counting (Bottom) -->
		<h3 class="h3 mb-2">Counting</h3>
		<div class="grid md:grid-cols-3 gap-2 mb-4">
			<label class="label">
				<span>ID Prefixes</span>
				<input class="input" type="text" bind:value={settings.counting.idPrefixes} />
			</label>
			<label class="label">
				<span>Start Counting Distance (m)</span>
				<input class="input" type="number" step="0.01" min="0" bind:value={settings.counting.minDistance} />
			</label>
			<label class="label">
				<span>Stop Counting Distance (m)</span>
				<input class="input" type="number" step="0.01" min="0" bind:value={settings.counting.maxDistance} />
			</label>
		</div>
	</div>
{:else}
	<div class="card p-4 text-center">
		<p>Node settings not available.</p>
	</div>
{/if}