<script lang="ts">
  import type { NodeSetting } from './types';

  export let settings: NodeSetting; // Assume parent handles loading and provides this
</script>

{#if settings}
<label class="label">
  <span>ID</span>
  <input class="input" type="text" disabled bind:value={settings.id} />
</label>
<label>
  <span>Name</span>
  <input class="input" type="text" bind:value={settings.name} />
</label>
<label>
  <span>Absorption</span>
  <input class="input" type="number" bind:value={settings.calibration.absorption} />
</label>
<label>
  <span>Rx Adj Rssi</span>
  <input class="input" type="number" bind:value={settings.calibration.rxAdjRssi} />
</label>
<label>
  <span>Tx Ref Rssi</span>
  <input class="input" type="number" bind:value={settings.calibration.txRefRssi} />
</label>
<label>
  <span>Max Distance</span>
  <input class="input" type="number" bind:value={settings.filtering.maxDistance} />
</label>
{:else}
  <div class="text-center text-surface-950-50">Node settings not available.</div>
{/if}
