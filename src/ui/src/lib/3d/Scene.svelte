<script lang="ts">
    import { T, useFrame } from '@threlte/core';
    import { OrbitControls } from '@threlte/extras';
    import { onMount } from 'svelte';
    import Rooms from './Rooms.svelte';
    import Nodes from './Nodes.svelte';
    import Devices from './Devices.svelte';
    import { GUI } from 'three/examples/jsm/libs/lil-gui.module.min.js';
    import * as THREE from 'three';

    let showNodes = true;
    let zRotationSpeed = 0.002;
    let groupRotation = { z: 10.2 };
    let startTime = Date.now();
    let container: HTMLDivElement;

    const effectController = {
        zRotationSpeed: 0.002,
        showNodes: true,
        refreshNodes: () => {
            showNodes = false;
            setTimeout(() => {
                showNodes = true;
            }, 100);
        }
    };

    onMount(() => {
        const gui = new GUI({ title: 'Settings' });
        gui.add(effectController, 'zRotationSpeed', 0, 1, 0.01)
            .onChange((value: number) => { zRotationSpeed = value; });
        gui.add(effectController, 'refreshNodes');
        gui.close();

        return () => {
            gui.destroy();
        };
    });

    // Update rotation based on elapsed time
    useFrame(() => {
        const elapsedTime = (Date.now() - startTime) / 1000;
        groupRotation.z = (elapsedTime * zRotationSpeed) % (Math.PI * 2);
    });
</script>

<div bind:this={container}>
    <!-- Scene setup -->
    <T.PerspectiveCamera
        position={[0, -40, 50]}
        fov={45}
        near={0.1}
        far={1000}
        makeDefault
    >
        <OrbitControls
            enableDamping
            dampingFactor={0.05}
            minDistance={35}
            maxDistance={80}
            target={[0, 0, 12]}
            maxPolarAngle={Math.PI * 0.45}
            minPolarAngle={Math.PI * 0.1}
        />
    </T.PerspectiveCamera>

    <!-- Scene background -->
    <T.Color args={[0x1e293b]} attach="background" />

    <!-- Main scene content -->
    <T.Group rotation.x={0.35} rotation.z={groupRotation.z}>
        <Rooms />
        {#if showNodes}
            <Nodes />
        {/if}
        <Devices />
    </T.Group>

    <!-- Lighting -->
    <T.AmbientLight intensity={0.7} />
    <T.DirectionalLight position={[10, 10, 20]} intensity={0.5} />
    <T.DirectionalLight position={[-10, -10, 20]} intensity={0.3} />
    <T.DirectionalLight position={[0, 0, 30]} intensity={0.2} />

    <!-- Add some fog for depth -->
    <T.Fog args={[0x1e293b, 60, 100]} attach="fog" />
</div>

<style>
    div :global(.lil-gui) {
        --font-family: Arial, sans-serif;
    }
</style>