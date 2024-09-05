<script>
import { page } from '$app/stores';
import { devices } from '$lib/stores';
import Map from '$lib/Map.svelte';
import { onMount } from 'svelte';

let deviceId = $page.params.deviceId;
let selectedFloor = null;
let selectedSpot = null;
let nodeDistances = [];
let rssiValues = {};
let includedNodes = {};
let currentRssiAt1m = null;
let calculatedRssiAt1m = null;
let floors = [];

$: {
    if ($devices) {
        floors = [...new Set($devices.map(device => device.floor))];
    }
}

onMount(async () => {
    const response = await fetch(`/api/device/${deviceId}`);
    const deviceData = await response.json();
    currentRssiAt1m = deviceData.rssiAt1m;
});

function handleSpotSelection(event) {
    selectedSpot = event.detail;
    calculateDistances();
    fetchRssiValues();
}

function calculateDistances() {
    nodeDistances = $devices
        .filter(device => device.floor === selectedFloor)
        .map(device => ({
            id: device.id,
            distance: Math.sqrt(
                Math.pow(device.x - selectedSpot.x, 2) +
                Math.pow(device.y - selectedSpot.y, 2)
            )
        }));
    includedNodes = Object.fromEntries(nodeDistances.map(node => [node.id, true]));
}

async function fetchRssiValues() {
    const promises = nodeDistances.map(async (node) => {
        const response = await fetch(`/api/node/${node.id}/rssi`);
        const data = await response.json();
        return { id: node.id, rssi: data.rssi };
    });
    const results = await Promise.all(promises);
    rssiValues = Object.fromEntries(results.map(({ id, rssi }) => [id, rssi]));
    updateCalculation();
}

function updateCalculation() {
    const includedNodeData = nodeDistances.filter(node => includedNodes[node.id]);
    if (includedNodeData.length === 0) {
        calculatedRssiAt1m = null;
        return;
    }
    
    const sumRssiAdjusted = includedNodeData.reduce((sum, node) => {
        const rssi = rssiValues[node.id];
        return sum + (rssi + 20 * Math.log10(node.distance));
    }, 0);
    
    calculatedRssiAt1m = sumRssiAdjusted / includedNodeData.length;
}

async function saveCalibration() {
    const response = await fetch(`/api/device/${deviceId}`, {
        method: 'PUT',
        headers: {
            'Content-Type': 'application/json',
        },
        body: JSON.stringify({ rssiAt1m: calculatedRssiAt1m }),
    });
    if (response.ok) {
        currentRssiAt1m = calculatedRssiAt1m;
        alert('Calibration saved successfully!');
    } else {
        alert('Failed to save calibration. Please try again.');
    }
}

function toggleNodeInclusion(nodeId) {
    includedNodes[nodeId] = !includedNodes[nodeId];
    updateCalculation();
}
</script>

<h1>Calibration for Device {deviceId}</h1>

<select bind:value={selectedFloor}>
    <option value={null}>Select a floor</option>
    {#each floors as floor}
        <option value={floor}>Floor {floor}</option>
    {/each}
</select>

{#if selectedFloor}
    <Map {selectedFloor} on:spotSelected={handleSpotSelection} />
{/if}

{#if selectedSpot}
    <h2>Node Distances and RSSI Values</h2>
    <table>
        <thead>
            <tr>
                <th>Node ID</th>
                <th>Distance (m)</th>
                <th>RSSI</th>
                <th>Include in Calculation</th>
            </tr>
        </thead>
        <tbody>
            {#each nodeDistances as node}
                <tr>
                    <td>{node.id}</td>
                    <td>{node.distance.toFixed(2)}</td>
                    <td>{rssiValues[node.id] || 'N/A'}</td>
                    <td>
                        <input
                            type="checkbox"
                            checked={includedNodes[node.id]}
                            on:change={() => toggleNodeInclusion(node.id)}
                        />
                    </td>
                </tr>
            {/each}
        </tbody>
    </table>

    <h2>Calibration Results</h2>
    <p>Current RSSI@1m: {currentRssiAt1m !== null ? currentRssiAt1m.toFixed(2) : 'N/A'} dBm</p>
    <p>Calculated RSSI@1m: {calculatedRssiAt1m !== null ? calculatedRssiAt1m.toFixed(2) : 'N/A'} dBm</p>

    <button on:click={saveCalibration} disabled={calculatedRssiAt1m === null}>
        Save Calibration
    </button>
{/if}

<style>
    h1, h2 {
        color: #333;
    }
    select, button {
        margin: 10px 0;
        padding: 5px;
    }
    table {
        border-collapse: collapse;
        width: 100%;
        margin-bottom: 20px;
    }
    th, td {
        border: 1px solid #ddd;
        padding: 8px;
        text-align: left;
    }
    th {
        background-color: #f2f2f2;
    }
    button {
        background-color: #4CAF50;
        color: white;
        border: none;
        padding: 10px 20px;
        text-align: center;
        text-decoration: none;
        display: inline-block;
        font-size: 16px;
        margin: 4px 2px;
        cursor: pointer;
    }
    button:disabled {
        background-color: #cccccc;
        cursor: not-allowed;
    }
</style>
