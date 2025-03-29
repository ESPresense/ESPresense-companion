<script lang="ts">
	import { onMount } from 'svelte';
	import * as THREE from 'three';
	import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
	import { CSS2DRenderer, CSS2DObject } from 'three/examples/jsm/renderers/CSS2DRenderer.js';
	import { GUI } from 'three/examples/jsm/libs/lil-gui.module.min.js';
	import { devices, nodes, config } from '$lib/stores';
	import type { Device, Node } from '$lib/types';
	import type { Group } from 'three';

	let container: HTMLDivElement;
	let scene: THREE.Scene;
	let camera: THREE.PerspectiveCamera;
	let renderer: THREE.WebGLRenderer;
	let labelRenderer: CSS2DRenderer;
	let controls: OrbitControls;
	let rotationPivot: THREE.Group;
	let contentGroup: THREE.Group;
	let isAnimating = false;
	let startTime: number | null = null;
	let guiInstance: GUI | null = null;
	let planCenter = new THREE.Vector3(0, 0, 0);

	// Device visualization state
	const PULSE_SPEED = 2;
	const PULSE_MIN = 0.8;
	const PULSE_MAX = 1.2;
	const geoSphere = new THREE.SphereGeometry(0.2, 32, 16);
	const trackerMaterials = [
		new THREE.MeshStandardMaterial({
			emissive: 0xff0000,
			emissiveIntensity: 2,
			transparent: true,
			opacity: 0.8
		}),
		new THREE.MeshStandardMaterial({
			emissive: 0xffbb00,
			emissiveIntensity: 2,
			transparent: true,
			opacity: 0.8
		}),
		new THREE.MeshStandardMaterial({
			emissive: 0xffee00,
			emissiveIntensity: 2,
			transparent: true,
			opacity: 0.8
		})
	];
	let deviceGroup: THREE.Group | null = null;
	let trackingSpheres: THREE.Mesh[] = [];
	let trackerLabels: { [key: string]: HTMLDivElement } = {};

	// Room visualization state
	const roomMaterials = {
		green1: new THREE.LineBasicMaterial({
			color: 0x03a062,
			transparent: true,
			opacity: 0.6
		})
	};
	const floorMaterial = new THREE.MeshBasicMaterial({
		color: 0x03a062,
		side: THREE.DoubleSide,
		opacity: 0.1,
		transparent: true
	});
	let roomGroup: THREE.Group | null = null;

	// Node visualization state
	const nodeMaterials = {
		online: new THREE.MeshPhongMaterial({
			color: 0x000000,
			emissive: 0x5555ff,
			emissiveIntensity: 2,
			shininess: 100,
			toneMapped: false
		}),
		offline: new THREE.MeshPhongMaterial({
			color: 0x000000,
			emissive: 0xff2222,
			emissiveIntensity: 2,
			shininess: 100,
			toneMapped: false
		})
	};
	let nodeGroup: THREE.Group | null = null;
	let showNodes = true;
	let lastTime: number | null = null;

	let zRotationSpeed = 0.002;

	const effectController = {
		zRotationSpeed: 0.002,
		showNodes: true
	};

	// Camera settings
	const CAM_START_X = 0;
	const CAM_START_Y = 0;
	const CAM_START_Z = 23;
	const CONTROLS_MIN_DISTANCE = 5;
	const CONTROLS_MAX_DISTANCE = 40;

	function initScene() {
		renderer = new THREE.WebGLRenderer({
			antialias: true,
			powerPreference: 'high-performance',
			stencil: false,
			depth: true
		});
		renderer.setPixelRatio(window.devicePixelRatio);
		renderer.setSize(container.clientWidth, container.clientHeight);
		renderer.setClearColor(0x1e293b, 1);
		renderer.autoClear = true;
		renderer.autoClearColor = true;
		renderer.autoClearDepth = true;
		container.appendChild(renderer.domElement);

		labelRenderer = new CSS2DRenderer();
		labelRenderer.setSize(container.clientWidth, container.clientHeight);
		labelRenderer.domElement.style.position = 'absolute';
		labelRenderer.domElement.style.top = '0px';
		labelRenderer.domElement.style.pointerEvents = 'none';
		labelRenderer.domElement.style.zIndex = '1';

		const existingLabels = container.querySelector('.css2d-renderer');
		if (existingLabels) {
			container.removeChild(existingLabels);
		}

		labelRenderer.domElement.classList.add('css2d-renderer');
		container.appendChild(labelRenderer.domElement);

		scene = new THREE.Scene();
		scene.background = new THREE.Color(0x1e293b);

		camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 1000);
		scene.add(camera);

		controls = new OrbitControls(camera, renderer.domElement);
		controls.enableDamping = true;
		controls.dampingFactor = 0.05;
		controls.minDistance = CONTROLS_MIN_DISTANCE;
		controls.maxDistance = CONTROLS_MAX_DISTANCE;
		controls.enablePan = true;

		const gl = renderer.getContext();
		gl.enable(gl.DEPTH_TEST);
		gl.depthFunc(gl.LEQUAL);

		rotationPivot = new THREE.Group();
		scene.add(rotationPivot);

		contentGroup = new THREE.Group();
		rotationPivot.add(contentGroup);

		setupRooms();
		camera.position.set(planCenter.x, planCenter.y, CAM_START_Z);
		controls.target.copy(planCenter);
		controls.update();

		setupNodes();
		setupDevices();

		doGuiSetup();
	}

	function calculateRoomCenter(points: THREE.Vector2[]) {
		const center = new THREE.Vector2();
		points.forEach((point) => center.add(point));
		center.divideScalar(points.length);
		return center;
	}

	function createLabelForRoom(name: string, points: THREE.Vector2[]) {
		const center = calculateRoomCenter(points);

		const labelDivEle = document.createElement('div');
		labelDivEle.style.color = '#ffffff';
		labelDivEle.style.fontFamily = 'Arial';
		labelDivEle.style.fontSize = '1rem';
		labelDivEle.style.textAlign = 'center';
		labelDivEle.style.pointerEvents = 'none';
		labelDivEle.textContent = name;

		const labelElement = new CSS2DObject(labelDivEle);
		labelElement.name = 'roomLabel';
		labelElement.position.set(center.x, center.y, 0);

		return labelElement;
	}

	function setupRooms() {
		if ($config?.floors && rotationPivot && contentGroup) {
			cleanupRooms();
			const newRoomGroup = new THREE.Group();
			newRoomGroup.name = 'RoomGroup';
			const overallBounds = new THREE.Box3();

			$config.floors.forEach((floor) => {
				const floor_base = floor.bounds[0][2];
				const floor_ceiling = floor.bounds[1][2];

				floor.rooms?.forEach((room: any) => {
					const points3d: THREE.Vector3[] = [];
					const pointsFloor: THREE.Vector2[] = [];

					room.points.forEach((points: number[]) => {
						const vec3Base = new THREE.Vector3(points[0], points[1], floor_base);
						const vec3Ceiling = new THREE.Vector3(points[0], points[1], floor_ceiling);

						points3d.push(vec3Base);
						points3d.push(vec3Ceiling);
						points3d.push(vec3Base);

						overallBounds.expandByPoint(vec3Base);
						overallBounds.expandByPoint(vec3Ceiling);

						pointsFloor.push(new THREE.Vector2(points[0], points[1]));
					});

					// Connect back to the first point to close the floor loop
					if (room.points.length > 0) {
						const firstPoint = room.points[0];
						points3d.push(new THREE.Vector3(firstPoint[0], firstPoint[1], floor_base));
					}

					// Create ceiling lines
					room.points.forEach((points: number[]) => {
						points3d.push(new THREE.Vector3(points[0], points[1], floor_ceiling));
					});

					// Close the ceiling loop as well
					if (room.points.length > 0) {
						const firstPoint = room.points[0];
						points3d.push(new THREE.Vector3(firstPoint[0], firstPoint[1], floor_ceiling));
					}

					const lines = new THREE.BufferGeometry().setFromPoints(points3d);
					const roomLine = new THREE.Line(lines, roomMaterials.green1);
					newRoomGroup.add(roomLine);

					const floorShape = new THREE.Shape(pointsFloor);
					const floorGeometry = new THREE.ShapeGeometry(floorShape);
					const plane = new THREE.Mesh(floorGeometry, floorMaterial);
					plane.position.z = floor_base;
					newRoomGroup.add(plane);

					const label = createLabelForRoom(room.name, pointsFloor);
					label.position.z = floor_base;
					newRoomGroup.add(label);
				});
			});

			contentGroup.add(newRoomGroup);
			roomGroup = newRoomGroup;

			if (!overallBounds.isEmpty()) {
				overallBounds.getCenter(planCenter);
			} else {
				planCenter.set(0, 0, 0);
			}

			rotationPivot.position.copy(planCenter);
			contentGroup.position.copy(planCenter).negate();
		}
	}

	function setupNodes() {
		if ($nodes && contentGroup && showNodes) {
			updateNodes($nodes);
		}

		if (!showNodes) {
			cleanupNodeGroup();
		}
	}

	function setupDevices() {
		if ($devices && contentGroup) {
			updateDevices($devices);
		}
	}

	function cleanupRooms() {
		if (roomGroup) {
			roomGroup.traverse((child) => {
				if ((child as any).geometry) {
					(child as any).geometry.dispose();
				}
				if ((child as any).material) {
					(child as any).material.dispose();
				}
			});
			contentGroup?.remove(roomGroup);
			roomGroup = null;
		}
	}

	function cleanupNodeGroup() {
		if (!nodeGroup) return;

		nodeGroup.traverse((child) => {
			if ((child as any).geometry) {
				(child as any).geometry.dispose();
			}
			if ((child as any).material) {
				(child as any).material.dispose();
			}
			if (child instanceof CSS2DObject) {
				const element = child.element;
				if (element && element.parentNode) {
					element.parentNode.removeChild(element);
				}
			}
		});

		contentGroup?.remove(nodeGroup);
		nodeGroup = null;
	}

	function cleanupDeviceGroup() {
		if (!deviceGroup) return;

		deviceGroup.traverse((child) => {
			if ((child as any).geometry) {
				(child as any).geometry.dispose();
			}
			if ((child as any).material) {
				(child as any).material.dispose();
			}
			if (child instanceof CSS2DObject) {
				const element = child.element;
				if (element && element.parentNode) {
					element.parentNode.removeChild(element);
				}
			}
		});

		contentGroup?.remove(deviceGroup);
		deviceGroup = null;
		trackingSpheres = [];
		trackerLabels = {};
	}

	function updateNodes(nodes: Node[]) {
		cleanupNodeGroup();

		const newNodeGroup = new THREE.Group();
		newNodeGroup.name = 'NodeGroup';

		nodes.forEach((node) => {
			if (!node.location) {
				console.warn('Node missing location:', node);
				return;
			}

			const mesh = new THREE.Mesh(new THREE.SphereGeometry(0.08, 32, 16), nodeMaterials[node.online ? 'online' : 'offline']);

			mesh.position.set(node.location.x, node.location.y, node.location.z);
			mesh.name = 'node#' + node.id;

			newNodeGroup.add(mesh);
			newNodeGroup.add(createLabelForNode(node));
		});

		nodeGroup = newNodeGroup;
		contentGroup.add(nodeGroup);
	}

	function createLabelForNode(node: Node) {
		const labelDivEle = document.createElement('div');
		labelDivEle.style.color = node.online ? '#5555ff' : '#dc2d2d';
		labelDivEle.style.fontFamily = 'Arial';
		labelDivEle.style.fontSize = '0.8rem';
		labelDivEle.style.marginTop = '-1em';

		const labelDivLine1 = document.createElement('div');
		labelDivLine1.textContent = node.name;
		labelDivEle.append(labelDivLine1);

		const labelElement = new CSS2DObject(labelDivEle);
		labelElement.name = 'nodeLabel';

		if (node.location) {
			labelElement.position.set(node.location.x, node.location.y, node.location.z);
		}

		return labelElement;
	}

	function updateDevices(devices: Device[]) {
		cleanupDeviceGroup();

		const newDeviceGroup = new THREE.Group();
		newDeviceGroup.name = 'DeviceGroup';

		devices.forEach((device) => {
			if (!device.location) return;

			const trackName = device.id;
			const confidence = device.confidence || 0;
			const fixes = device.fixes || 0;
			const position = device.location;

			if (confidence <= 1) return;

			const material = trackerMaterials[trackingSpheres.length % trackerMaterials.length];
			const newSphere = new THREE.Mesh(geoSphere, material);
			newSphere.name = trackName;
			newSphere.position.set(position.x, position.y, position.z);

			trackingSpheres.push(newSphere);
			newDeviceGroup.add(newSphere);

			const labelDivEle = document.createElement('div');
			labelDivEle.style.color = '#ffffff';
			labelDivEle.style.fontFamily = 'Arial';
			labelDivEle.style.fontSize = '0.8rem';
			labelDivEle.style.marginTop = '-1em';

			const labelDivLine1 = document.createElement('div');
			const displayName = device.name || device.id;
			labelDivLine1.textContent = displayName.length > 15 ? displayName.substring(0, 14) + '...' : displayName;

			const labelDivLine2 = document.createElement('div');
			labelDivLine2.textContent = `${confidence}% (${fixes} fixes)`;

			labelDivEle.append(labelDivLine1, labelDivLine2);
			trackerLabels[trackName] = labelDivLine2;

			const labelElement = new CSS2DObject(labelDivEle);
			labelElement.name = trackName + '#label';
			labelElement.position.set(position.x, position.y, position.z);
			newDeviceGroup.add(labelElement);
		});

		deviceGroup = newDeviceGroup;
		contentGroup.add(deviceGroup);
	}

	function updatePulse() {
		const elapsed = (performance.now() - startTime) / 1000;
		const phase = (elapsed * PULSE_SPEED * Math.PI) % (Math.PI * 2);
		const scale = PULSE_MIN + ((Math.sin(phase) + 1) * (PULSE_MAX - PULSE_MIN)) / 2;

		trackingSpheres.forEach((sphere) => {
			sphere.scale.set(scale, scale, scale);
		});
	}

	function animate(currentTime: number) {
		if (!isAnimating) return;

		if (lastTime === null) {
			lastTime = currentTime;
		}

		const deltaTime = (currentTime - lastTime) * 0.001;

		controls.update();

		rotationPivot.rotation.z += deltaTime * zRotationSpeed;
		rotationPivot.rotation.z %= Math.PI * 2;

		if (!startTime) startTime = currentTime;
		updatePulse();

		renderer.clear(true, true, true);
		renderer.render(scene, camera);
		labelRenderer.render(scene, camera);

		lastTime = currentTime;

		requestAnimationFrame(animate);
	}

	function doGuiSetup() {
		guiInstance?.destroy();
		guiInstance = new GUI({ title: 'Settings' });

		guiInstance.add(effectController, 'zRotationSpeed', 0, Math.PI / 2, 0.01).onChange((value: number) => {
			zRotationSpeed = value;
		});

		guiInstance.add(effectController, 'showNodes').onChange((value: boolean) => {
			showNodes = value;
			if (showNodes) {
				if ($nodes) updateNodes($nodes);
			} else {
				cleanupNodeGroup();
			}
		});

		guiInstance.close();
	}

	onMount(() => {
		if (container) {
			initScene();
			isAnimating = true;
			requestAnimationFrame(animate);
		}

		const handleResize = () => {
			if (!camera || !renderer || !labelRenderer) return;

			const width = container.clientWidth;
			const height = container.clientHeight;

			camera.aspect = width / height;
			camera.updateProjectionMatrix();

			renderer.setSize(width, height);
			renderer.setPixelRatio(window.devicePixelRatio);

			labelRenderer.setSize(width, height);
		};

		window.addEventListener('resize', handleResize);

		return () => {
			isAnimating = false;
			cleanupRooms();
			cleanupNodeGroup();
			cleanupDeviceGroup();
			window.removeEventListener('resize', handleResize);
			if (renderer) {
				renderer.dispose();
				renderer.forceContextLoss();
			}
			guiInstance?.destroy();
		};
	});
</script>

<svelte:head>
	<title>ESPresense Companion: 3D Map</title>
</svelte:head>

<div class="w-full h-full" bind:this={container}></div>

<style>
	div {
		background-color: rgb(30, 41, 59);
	}
</style>
