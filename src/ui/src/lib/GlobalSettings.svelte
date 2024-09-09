<script lang="ts">
    import TriStateCheckbox from '$lib/TriStateCheckbox.svelte';
    import { settings } from '$lib/stores';
    import { onMount } from 'svelte';

    let loading = true;
    let error: string | null = null;

    onMount(async () => {
        try {
            await settings.load();
        } catch (e: unknown) {
            error = e instanceof Error ? e.message : 'An unknown error occurred';
        } finally {
            loading = false;
        }
    });

    async function handleUpdate() {
        try {
            if ($settings) {
                await settings.save($settings);
            }
        } catch (e: unknown) {
            error = e instanceof Error ? `Error updating settings: ${e.message}` : 'An unknown error occurred while updating settings';
        }
    }
</script>

{#if loading}
    <div class="card m-2 p-4 variant-filled-surface">
        <div class="flex items-center space-x-4">
            <span class="loading loading-spinner loading-lg" />
            <p>Loading settings...</p>
        </div>
    </div>
{:else if error}
    <div class="card m-2 p-4 variant-filled-error">
        <p>Error: {error}</p>
    </div>
{:else if $settings}
    <div class="card m-2 p-4 variant-filled-surface">
        <section class="mb-8">
            <h2 class="text-2xl font-semibold mb-4">Updating</h2>
            <div class="space-y-4 ml-4">
                <div class="flex items-center space-x-2">
                    <TriStateCheckbox id="auto-update" bind:checked={$settings.updating.autoUpdate} />
                    <label for="auto-update">Automatically update</label>
                </div>
                <div class="flex items-center space-x-2">
                    <TriStateCheckbox id="pre-release" bind:checked={$settings.updating.preRelease} />
                    <label for="pre-release">Include pre-released versions in auto-update</label>
                </div>
            </div>
        </section>

        <section class="mb-8">
            <h2 class="text-2xl font-semibold mb-4">Scanning</h2>
            <div class="space-y-4 ml-4">
                <label class="label">
                    <span>Forget beacon if not seen for (in milliseconds):</span>
                    <input type="number" class="input" min="0" bind:value={$settings.scanning.forgetAfterMs} placeholder="150000" />
                </label>
            </div>
        </section>

        <section class="mb-8">
            <h2 class="text-2xl font-semibold mb-4">Counting</h2>
            <div class="space-y-4 ml-4">
                <label class="label">
                    <span>Include id prefixes (space separated):</span>
                    <input type="text" class="input" bind:value={$settings.counting.idPrefixes} placeholder="" />
                </label>

                <label class="label">
                    <span>Start counting devices less than distance (in meters):</span>
                    <input type="number" class="input" step="0.01" min="0" bind:value={$settings.counting.startCountingDistance} placeholder="2.00" />
                </label>

                <label class="label">
                    <span>Stop counting devices greater than distance (in meters):</span>
                    <input type="number" class="input" step="0.01" min="0" bind:value={$settings.counting.stopCountingDistance} placeholder="4.00" />
                </label>

                <label class="label">
                    <span>Include devices with age less than (in ms):</span>
                    <input type="number" class="input" min="0" bind:value={$settings.counting.includeDevicesAge} placeholder="30000" />
                </label>
            </div>
        </section>

        <section class="mb-8">
            <h2 class="text-2xl font-semibold mb-4">Filtering</h2>
            <div class="space-y-4 ml-4">
                <label class="label">
                    <span>Include only sending these ids to mqtt (eg. apple:iphone10-6 apple:iphone13-2):</span>
                    <input type="text" class="input" bind:value={$settings.filtering.includeIds} placeholder="" />
                </label>

                <label class="label">
                    <span>Exclude sending these ids to mqtt (eg. exp:20 apple:iphone10-6):</span>
                    <input type="text" class="input" bind:value={$settings.filtering.excludeIds} placeholder="" />
                </label>

                <label class="label">
                    <span>Max report distance (in meters):</span>
                    <input type="number" class="input" step="0.01" min="0" bind:value={$settings.filtering.maxReportDistance} placeholder="16.00" />
                </label>

                <label class="label">
                    <span>Report early if beacon has moved more than this distance (in meters):</span>
                    <input type="number" class="input" step="0.01" min="0" bind:value={$settings.filtering.earlyReportDistance} placeholder="0.50" />
                </label>

                <label class="label">
                    <span>Skip reporting if message age is less that this (in milliseconds):</span>
                    <input type="number" class="input" min="0" bind:value={$settings.filtering.skipReportAge} placeholder="5000" />
                </label>
            </div>
        </section>

        <section class="mb-8">
            <h2 class="text-2xl font-semibold mb-4">Calibration</h2>
            <div class="space-y-4 ml-4">
                <label class="label">
                    <span>Rssi expected from a 0dBm transmitter at 1 meter (NOT used for iBeacons or Eddystone):</span>
                    <input type="number" class="input" bind:value={$settings.calibration.rssiAt1m} placeholder="-65" />
                </label>

                <label class="label">
                    <span>Rssi adjustment for receiver (use only if you know this device has a weak antenna):</span>
                    <input type="number" class="input" bind:value={$settings.calibration.rssiAdjustment} placeholder="0" />
                </label>

                <label class="label">
                    <span>Factor used to account for absorption, reflection, or diffraction:</span>
                    <input type="number" class="input" step="0.01" min="0" bind:value={$settings.calibration.absorptionFactor} placeholder="3.50" />
                </label>

                <label class="label">
                    <span>Rssi expected from this tx power at 1m (used for node iBeacon):</span>
                    <input type="number" class="input" bind:value={$settings.calibration.iBeaconRssiAt1m} placeholder="-59" />
                </label>
            </div>
        </section>

        <div class="flex justify-end mt-8">
            <button class="btn variant-filled-primary" on:click={handleUpdate}>Update Settings</button>
        </div>
    </div>
{:else}
    <div class="card p-4 variant-filled-warning">
        <p>No settings available.</p>
    </div>
{/if}