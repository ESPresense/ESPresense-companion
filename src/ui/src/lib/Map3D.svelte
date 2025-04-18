<script lang="ts">
	// Component to render the 3D map scene
	import { onMount, onDestroy } from 'svelte';
	import * as THREE from 'three';
	import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
	import { CSS2DRenderer, CSS2DObject } from 'three/examples/jsm/renderers/CSS2DRenderer.js';
	import type { Device, Node, Config, DeviceHistory } from '$lib/types';
	import type { Group } from 'three';
	import { detail3d } from '$lib/urls';

	// --- Props ---
	export let devicesToShow: Device[] = [];
	export let nodesToShow: Node[] = [];
	export let config: Config | null = null;
	export let historyData: DeviceHistory[] = [];
	export let showNodes: boolean = true;
	export let showDevices: boolean = true;
	export let showHistoryPath: boolean = false;
	export let zRotationSpeed: number = 0.002;

	// --- Internal State ---
	let container: HTMLDivElement;
	let scene: THREE.Scene;
	let camera: THREE.PerspectiveCamera;
	let renderer: THREE.WebGLRenderer;
	let labelRenderer: CSS2DRenderer;
	let controls: OrbitControls;
	let rotationPivot: THREE.Group;
	let contentGroup: THREE.Group;
	let isAnimating = false;
	let animationFrameId: number;
	let startTime: number | null = null; // For pulsing animation
	let lastTime: number | null = null; // For rotation animation delta
	let planCenter = new THREE.Vector3(0, 0, 0);
	let raycaster = new THREE.Raycaster(); // Raycaster for click detection
	let mouse = new THREE.Vector2(); // Mouse coordinates for raycaster

	// Device visualization state
	const PULSE_SPEED = 2;
	const PULSE_MIN = 0.8;
	const PULSE_MAX = 1.2;
	const geoSphere = new THREE.SphereGeometry(0.2, 32, 16); // Reusable geometry
	const trackerMaterials = [ // Cycle through materials
		new THREE.MeshStandardMaterial({ emissive: 0xff0000, emissiveIntensity: 2, transparent: true, opacity: 0.8 }),
		new THREE.MeshStandardMaterial({ emissive: 0xffbb00, emissiveIntensity: 2, transparent: true, opacity: 0.8 }),
		new THREE.MeshStandardMaterial({ emissive: 0xffee00, emissiveIntensity: 2, transparent: true, opacity: 0.8 })
	];
	let deviceGroup: THREE.Group | null = null;
	let trackingSpheres: THREE.Mesh[] = []; // Still need this for pulsing scale updates
	// Map to store device labels for efficient updates
	let deviceLabels: { [id: string]: { label: CSS2DObject; element: HTMLDivElement; line1: HTMLDivElement; line2: HTMLDivElement } } = {};


	// Room visualization state
	const roomMaterials = {
		green1: new THREE.LineBasicMaterial({ color: 0x03a062, transparent: true, opacity: 0.6 })
	};
	const floorMaterial = new THREE.MeshBasicMaterial({ color: 0x03a062, side: THREE.DoubleSide, opacity: 0.1, transparent: true });
	let roomGroup: THREE.Group | null = null;

	// Node visualization state
	const nodeMaterials = {
		online: new THREE.MeshPhongMaterial({ color: 0x000000, emissive: 0x5555ff, emissiveIntensity: 2, shininess: 100, toneMapped: false }),
		offline: new THREE.MeshPhongMaterial({ color: 0x000000, emissive: 0xff2222, emissiveIntensity: 2, shininess: 100, toneMapped: false })
	};
	let nodeGroup: THREE.Group | null = null;
	// Map to store node labels for efficient updates
	let nodeLabels: { [id: string]: { label: CSS2DObject; element: HTMLDivElement } } = {};

	// History path state
	const historyPathMaterial = new THREE.LineBasicMaterial({ color: 0xffffff, linewidth: 2 });
	let historyPathLine: THREE.Line | null = null;

	// Camera settings
	const CAM_START_Z = 23;
	const CONTROLS_MIN_DISTANCE = 5;
	const CONTROLS_MAX_DISTANCE = 40;

	// --- Lifecycle ---
	// Watch for prop changes to update the scene
	$: if (scene && contentGroup) updateSceneObjects(devicesToShow, nodesToShow, historyData, showDevices, showNodes, showHistoryPath);
	$: if (config && scene && contentGroup) setupRooms(); // Re-setup rooms if config changes

	onMount(() => {
		if (container) {
			initScene();
			isAnimating = true;
			animate(performance.now()); // Start animation loop

			// Add click listener for navigation
			container.addEventListener('click', onCanvasClick);
		}

		const handleResize = () => {
			if (!camera || !renderer || !labelRenderer || !container) return;
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
			if (animationFrameId) cancelAnimationFrame(animationFrameId);
			window.removeEventListener('resize', handleResize);
			container?.removeEventListener('click', onCanvasClick); // Remove click listener
			cleanupScene();
		};
	});

	// --- Scene Initialization ---
	function initScene() {
		// Renderer
		renderer = new THREE.WebGLRenderer({
			antialias: true,
			powerPreference: 'high-performance',
			stencil: false,
			depth: true,
			preserveDrawingBuffer: false // Explicitly disable preserving buffer
		});
		renderer.setPixelRatio(window.devicePixelRatio);
		renderer.setSize(container.clientWidth, container.clientHeight);
		renderer.setClearColor(0x1e293b, 1); // Tailwind slate-800
		renderer.autoClear = false; // Rely on explicit clear in animate loop
		container.appendChild(renderer.domElement);

		// Label Renderer
		labelRenderer = new CSS2DRenderer();
		labelRenderer.setSize(container.clientWidth, container.clientHeight);
		labelRenderer.domElement.style.position = 'absolute';
		labelRenderer.domElement.style.top = '0px';
		labelRenderer.domElement.style.pointerEvents = 'none';
		labelRenderer.domElement.style.zIndex = '1';
		labelRenderer.domElement.classList.add('css2d-renderer-map'); // Unique class
		container.appendChild(labelRenderer.domElement);

		// Scene
		scene = new THREE.Scene();
		scene.background = new THREE.Color(0x1e293b);

		// Camera
		camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 1000);
		scene.add(camera); // Add camera to scene

		// Controls
		controls = new OrbitControls(camera, renderer.domElement);
		controls.enableDamping = true;
		controls.dampingFactor = 0.05;
		controls.minDistance = CONTROLS_MIN_DISTANCE;
		controls.maxDistance = CONTROLS_MAX_DISTANCE;
		controls.enablePan = true;

		// Depth Test
		const gl = renderer.getContext();
		gl.enable(gl.DEPTH_TEST);
		gl.depthFunc(gl.LEQUAL);

		// Rotation Pivot & Content Group (for centering)
		rotationPivot = new THREE.Group();
		scene.add(rotationPivot);
		contentGroup = new THREE.Group();
		rotationPivot.add(contentGroup);

		// Initial Setup
		setupRooms(); // Setup rooms first to calculate center
		camera.position.set(planCenter.x, planCenter.y, CAM_START_Z);
		controls.target.copy(planCenter);
		controls.update();

		// Initial object rendering based on props
		updateSceneObjects(devicesToShow, nodesToShow, historyData, showDevices, showNodes, showHistoryPath);
	}

	// --- Scene Updates ---
	function updateSceneObjects(
		currentDevices: Device[],
		currentNodes: Node[],
		currentHistory: DeviceHistory[],
		shouldShowDevices: boolean,
		shouldShowNodes: boolean,
		shouldShowHistory: boolean
	) {
		if (!contentGroup) return;

		// Devices
		if (shouldShowDevices) {
			renderDevices(currentDevices);
		} else {
			cleanupDeviceGroup(); // This now also cleans up deviceLabels map and DOM elements
		}

		// Nodes
		if (shouldShowNodes) {
			renderNodes(currentNodes);
		} else {
			cleanupNodeGroup(); // This now also cleans up nodeLabels map and DOM elements
		}

		// History Path
		if (shouldShowHistory) {
			renderHistoryPath(currentHistory);
		} else {
			cleanupHistoryPath();
		}
	}


	// --- Room Rendering ---
	function calculateRoomCenter(points: THREE.Vector2[]) {
		const center = new THREE.Vector2();
		points.forEach((point) => center.add(point));
		center.divideScalar(points.length);
		return center;
	}

	function createLabelForRoom(name: string, points: THREE.Vector2[]) {
		const center = calculateRoomCenter(points);
		const labelDivEle = document.createElement('div');
		labelDivEle.className = 'text-white text-sm text-center pointer-events-none'; // Use classes
		labelDivEle.textContent = name;
		const labelElement = new CSS2DObject(labelDivEle);
		labelElement.name = 'roomLabel';
		labelElement.position.set(center.x, center.y, 0);
		return labelElement;
	}

	function setupRooms() {
		if (!config?.floors || !rotationPivot || !contentGroup) return;

		cleanupRooms(); // Clear existing rooms first
		const newRoomGroup = new THREE.Group();
		newRoomGroup.name = 'RoomGroup';
		const overallBounds = new THREE.Box3();

		config.floors.forEach((floor) => {
			const floor_base = floor.bounds[0][2];
			const floor_ceiling = floor.bounds[1][2];

			floor.rooms?.forEach((room: any) => { // TODO: Use proper Room type if available
				const points3d: THREE.Vector3[] = [];
				const pointsFloor: THREE.Vector2[] = [];

				room.points.forEach((points: number[]) => {
					const vec3Base = new THREE.Vector3(points[0], points[1], floor_base);
					const vec3Ceiling = new THREE.Vector3(points[0], points[1], floor_ceiling);
					points3d.push(vec3Base, vec3Ceiling, vec3Base); // Base -> Ceiling -> Base (for vertical lines)
					overallBounds.expandByPoint(vec3Base);
					overallBounds.expandByPoint(vec3Ceiling);
					pointsFloor.push(new THREE.Vector2(points[0], points[1]));
				});

				// Connect back to the first point to close the floor loop
				if (room.points.length > 0) {
					const firstPoint = room.points[0];
					points3d.push(new THREE.Vector3(firstPoint[0], firstPoint[1], floor_base));
				}

				// Create ceiling lines (separate loop for clarity)
				room.points.forEach((points: number[]) => {
					points3d.push(new THREE.Vector3(points[0], points[1], floor_ceiling));
				});
				// Close the ceiling loop
				if (room.points.length > 0) {
					const firstPoint = room.points[0];
					points3d.push(new THREE.Vector3(firstPoint[0], firstPoint[1], floor_ceiling));
				}

				// Walls
				const lines = new THREE.BufferGeometry().setFromPoints(points3d);
				const roomLine = new THREE.Line(lines, roomMaterials.green1);
				newRoomGroup.add(roomLine);

				// Floor plane
				const floorShape = new THREE.Shape(pointsFloor);
				const floorGeometry = new THREE.ShapeGeometry(floorShape);
				const plane = new THREE.Mesh(floorGeometry, floorMaterial);
				plane.position.z = floor_base; // Position floor at its base Z
				newRoomGroup.add(plane);

				// Room Label
				const label = createLabelForRoom(room.name, pointsFloor);
				label.position.z = floor_base; // Position label slightly above floor
				newRoomGroup.add(label);
			});
		});

		contentGroup.add(newRoomGroup);
		roomGroup = newRoomGroup;

		// Recalculate center and adjust pivot/content group positions
		if (!overallBounds.isEmpty()) {
			overallBounds.getCenter(planCenter);
		} else {
			planCenter.set(0, 0, 0); // Default center if no rooms
		}
		rotationPivot.position.copy(planCenter);
		contentGroup.position.copy(planCenter).negate();

        // Update controls target after centering
        if (controls) {
            controls.target.copy(planCenter);
            controls.update();
        }
	}

	// --- Node Rendering ---
	function renderNodes(nodes: Node[]) {
		if (!contentGroup) return;

		// Ensure nodeGroup exists
		if (!nodeGroup) {
			nodeGroup = new THREE.Group();
			nodeGroup.name = 'NodeGroup';
			contentGroup.add(nodeGroup);
		}

		const existingNodeIds = new Set(Object.keys(nodeLabels));
		const currentNodeIds = new Set<string>();

		nodes.forEach((node) => {
			currentNodeIds.add(node.id);
			if (!node.location) {
				console.warn('Node missing location:', node);
				return;
			}

			// --- Sphere ---
			let mesh = nodeGroup?.getObjectByName('node#' + node.id) as THREE.Mesh | undefined;
			const material = nodeMaterials[node.online ? 'online' : 'offline'];
			if (mesh) {
				// Update existing mesh
				mesh.position.set(node.location.x, node.location.y, node.location.z);
				if (mesh.material !== material) {
					mesh.material = material; // Update material if status changed
				}
			} else {
				// Create new mesh
				mesh = new THREE.Mesh(new THREE.SphereGeometry(0.08, 32, 16), material);
				mesh.position.set(node.location.x, node.location.y, node.location.z);
				mesh.name = 'node#' + node.id;
				nodeGroup?.add(mesh); // Check if nodeGroup exists
			}

			// --- Label ---
			const labelInfo = nodeLabels[node.id];
			if (labelInfo) {
				// Update existing label
				labelInfo.label.position.set(node.location.x, node.location.y, node.location.z);
				updateNodeLabelElement(labelInfo.element, node); // Update content/style
			} else {
				// Create new label
				const newLabelElement = createNodeLabelElement(node);
				const newLabel = new CSS2DObject(newLabelElement);
				newLabel.name = 'nodeLabel#' + node.id;
				newLabel.position.set(node.location.x, node.location.y, node.location.z);
				nodeLabels[node.id] = { label: newLabel, element: newLabelElement };
				nodeGroup?.add(newLabel); // Check if nodeGroup exists
			}
		});

		// Remove labels/meshes for nodes that are no longer present
		existingNodeIds.forEach(nodeId => {
			if (!currentNodeIds.has(nodeId)) {
				// Remove label
				const labelToRemove = nodeLabels[nodeId];
				if (labelToRemove) {
					labelToRemove.element.remove(); // Remove from DOM
					nodeGroup?.remove(labelToRemove.label); // Remove from scene graph
					delete nodeLabels[nodeId]; // Remove from map
				}
				// Remove mesh
				const meshToRemove = nodeGroup?.getObjectByName('node#' + nodeId);
				if (meshToRemove) {
					nodeGroup?.remove(meshToRemove);
					// Dispose geometry/material if needed, handled by cleanupScene? Check later.
				}
			}
		});
	}

	// Creates/updates the HTML element for a node label
	function updateNodeLabelElement(element: HTMLDivElement, node: Node) {
		element.textContent = node.name;
		element.className = `text-xs pointer-events-none ${node.online ? 'text-blue-400' : 'text-red-500'}`;
		// Keep margin-top style
	}

	// Creates the initial HTML element for a node label
	function createNodeLabelElement(node: Node): HTMLDivElement {
		const labelDivEle = document.createElement('div');
		labelDivEle.style.marginTop = '-1em'; // Position above the node
		updateNodeLabelElement(labelDivEle, node); // Set initial content/style
		return labelDivEle;
	}

	// --- Device Rendering ---
	function renderDevices(devices: Device[]) {
		if (!contentGroup) return;

		// Ensure deviceGroup exists
		if (!deviceGroup) {
			deviceGroup = new THREE.Group();
			deviceGroup.name = 'DeviceGroup';
			contentGroup.add(deviceGroup);
		}

		const existingDeviceIds = new Set(Object.keys(deviceLabels));
		const currentDeviceIds = new Set<string>();
		const localTrackingSpheres: THREE.Mesh[] = []; // Rebuild this list each time for pulsing

		devices.forEach((device) => {
			if (!device.location || (device.confidence || 0) <= 1) return; // Skip if no location or low confidence

			currentDeviceIds.add(device.id);
			const trackName = device.id;

			// --- Sphere ---
			let sphere = deviceGroup?.getObjectByName(trackName) as THREE.Mesh | undefined;
			const material = trackerMaterials[localTrackingSpheres.length % trackerMaterials.length]; // Assign material based on current count
			if (sphere) {
				// Update existing sphere
				sphere.position.set(device.location.x, device.location.y, device.location.z);
				// Potentially update material if needed, though cycling might be intended
				if (sphere.material !== material) {
					// This logic might need adjustment if material cycling per frame isn't desired
					// sphere.material = material;
				}
			} else {
				// Create new sphere
				sphere = new THREE.Mesh(geoSphere, material); // Reuse geometry
				sphere.name = trackName; // Store device ID in name for click detection
				sphere.position.set(device.location.x, device.location.y, device.location.z);
				deviceGroup?.add(sphere); // Check if deviceGroup exists
			}
			localTrackingSpheres.push(sphere); // Add to list for pulsing

			// --- Label ---
			const labelInfo = deviceLabels[trackName];
			if (labelInfo) {
				// Update existing label
				labelInfo.label.position.set(device.location.x, device.location.y, device.location.z);
				updateDeviceLabelElement(labelInfo.element, labelInfo.line1, labelInfo.line2, device);
			} else {
				// Create new label
				const { element, line1, line2 } = createDeviceLabelElement(device);
				const newLabel = new CSS2DObject(element);
				newLabel.name = trackName + '#label';
				newLabel.position.set(device.location.x, device.location.y, device.location.z);
				deviceLabels[trackName] = { label: newLabel, element, line1, line2 };
				deviceGroup?.add(newLabel); // Check if deviceGroup exists
			}
		});

		// Update component-level tracking spheres for pulsing
		trackingSpheres = localTrackingSpheres;

		// Remove labels/spheres for devices that are no longer present
		existingDeviceIds.forEach(deviceId => {
			if (!currentDeviceIds.has(deviceId)) {
				// Remove label
				const labelToRemove = deviceLabels[deviceId];
				if (labelToRemove) {
					labelToRemove.element.remove(); // Remove from DOM
					deviceGroup?.remove(labelToRemove.label); // Remove from scene graph
					delete deviceLabels[deviceId]; // Remove from map
				}
				// Remove sphere
				const sphereToRemove = deviceGroup?.getObjectByName(deviceId);
				if (sphereToRemove) {
					deviceGroup?.remove(sphereToRemove); // Remove from scene graph
				}
			}
		});
	}

	// Creates/updates the HTML element for a device label
	function updateDeviceLabelElement(element: HTMLDivElement, line1: HTMLDivElement, line2: HTMLDivElement, device: Device) {
		const displayName = device.name || device.id;
		line1.textContent = displayName.length > 15 ? displayName.substring(0, 14) + '...' : displayName;
		line2.textContent = `${device.confidence}% (${device.fixes} fixes)`;
		// Class and margin-top are set once on creation
	}

	// Creates the initial HTML element for a device label
	function createDeviceLabelElement(device: Device): { element: HTMLDivElement; line1: HTMLDivElement; line2: HTMLDivElement } {
		const element = document.createElement('div');
		element.className = 'text-white text-xs pointer-events-none';
		element.style.marginTop = '-1em';

		const line1 = document.createElement('div');
		const line2 = document.createElement('div');

		element.append(line1, line2);
		updateDeviceLabelElement(element, line1, line2, device); // Set initial content
		return { element, line1, line2 };
	}


	function updatePulse() {
		if (startTime === null || trackingSpheres.length === 0) return;
		const elapsed = (performance.now() - startTime) / 1000;
		const phase = (elapsed * PULSE_SPEED * Math.PI) % (Math.PI * 2);
		const scale = PULSE_MIN + ((Math.sin(phase) + 1) * (PULSE_MAX - PULSE_MIN)) / 2;
		trackingSpheres.forEach((sphere) => sphere.scale.set(scale, scale, scale));
	}

	// --- History Path Rendering ---
	function renderHistoryPath(history: DeviceHistory[]) {
		cleanupHistoryPath(); // Clear previous path
		if (!contentGroup || history.length < 2) return;

		const points = history
			.filter((h) => h.location)
			.map((h) => new THREE.Vector3(h.location.x, h.location.y, h.location.z));

		if (points.length > 1) {
			const geometry = new THREE.BufferGeometry().setFromPoints(points);
			historyPathLine = new THREE.Line(geometry, historyPathMaterial);
			historyPathLine.name = 'HistoryPathLine';
			contentGroup.add(historyPathLine);
		}
	}

	// --- Click Handling ---
	function onCanvasClick(event: MouseEvent) {
		if (!camera || !container || !deviceGroup) return;

		// Calculate mouse position in normalized device coordinates (-1 to +1)
		const rect = container.getBoundingClientRect();
		mouse.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
		mouse.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;

		// Update the picking ray with the camera and mouse position
		raycaster.setFromCamera(mouse, camera);

		// Calculate objects intersecting the picking ray
		const intersects = raycaster.intersectObjects(deviceGroup.children, false); // Only check device spheres

		if (intersects.length > 0) {
			const firstIntersected = intersects[0].object;
			// Check if it's a mesh (our sphere) and has a name (our device ID)
			if (firstIntersected instanceof THREE.Mesh && firstIntersected.name) {
				const deviceId = firstIntersected.name;
				detail3d(deviceId);
			}
		}
	}

	// --- Animation Loop ---
	function animate(currentTime: number) {
		if (!isAnimating) return;

		animationFrameId = requestAnimationFrame(animate); // Request next frame first

		if (lastTime === null) lastTime = currentTime;
		const deltaTime = (currentTime - lastTime) * 0.001; // Delta time in seconds

		controls?.update(); // Re-enable controls update

		// Rotation
		if (rotationPivot && zRotationSpeed !== 0) {
			rotationPivot.rotation.z += deltaTime * zRotationSpeed;
			rotationPivot.rotation.z %= Math.PI * 2; // Keep rotation within 0-2PI
		}

		// Pulse animation for devices
		if (showDevices) {
			if (startTime === null) startTime = currentTime; // Initialize pulse timer only when needed
			updatePulse();
		} else {
			startTime = null; // Reset pulse timer if devices are hidden
		}

		// Render scene
		renderer?.clear(true, true, true); // Explicitly clear buffers to prevent trails
		renderer?.render(scene, camera);
		labelRenderer?.render(scene, camera); // Keep label rendering enabled

		lastTime = currentTime; // Update last time for next frame
	}

	// --- Cleanup ---
	function cleanupScene() {
		// Dispose geometries, materials, textures
		scene?.traverse((object) => {
			if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
				object.geometry?.dispose();
				// Dispose material(s) carefully, especially if shared
				if (Array.isArray(object.material)) {
					object.material.forEach(material => material.dispose());
				} else if (object.material) {
					object.material.dispose();
				}
			}
			// Note: CSS2DObject elements are removed in specific cleanup functions below
		});

		// Ensure our label maps are cleared and DOM elements removed during full cleanup
		Object.values(nodeLabels).forEach(info => info.element.remove());
		nodeLabels = {};
		Object.values(deviceLabels).forEach(info => info.element.remove());
		deviceLabels = {};

		// Call specific group cleanups which also handle map clearing now
		cleanupRooms();
		cleanupNodeGroup();
		cleanupDeviceGroup();
		cleanupHistoryPath();

		// Dispose renderers and context
		renderer?.dispose();
		renderer?.forceContextLoss(); // Important for WebGL context release
		labelRenderer?.domElement?.remove(); // Remove label renderer DOM element

		// Nullify references
		roomGroup = null;
		nodeGroup = null;
		deviceGroup = null;
		historyPathLine = null;
		trackingSpheres = [];
	}

	function cleanupRooms() {
		if (roomGroup) {
			// Room labels are CSS2DObjects but managed directly within roomGroup
			// Need to ensure their elements are removed if not handled by scene traverse
			roomGroup.traverse((object) => {
				if (object instanceof CSS2DObject) {
					object.element?.remove();
				}
			});
			contentGroup?.remove(roomGroup);
			roomGroup = null;
		}
	}

	function cleanupNodeGroup() {
		if (nodeGroup) {
			contentGroup?.remove(nodeGroup); // Remove group from scene first
		}
		// Clear the label map and remove elements from DOM
		// Do this even if nodeGroup was already null or removed
		if (Object.keys(nodeLabels).length > 0) {
			Object.values(nodeLabels).forEach(info => {
				// Ensure label is removed from group *before* element removal if group still exists
				nodeGroup?.remove(info.label);
				info.element.remove();
			});
			nodeLabels = {};
		}
		nodeGroup = null; // Ensure group is nullified
	}

	function cleanupDeviceGroup() {
		if (deviceGroup) {
			contentGroup?.remove(deviceGroup); // Remove group from scene first
		}
		// Clear the label map and remove elements from DOM
		// Do this even if deviceGroup was already null or removed
		if (Object.keys(deviceLabels).length > 0) {
			Object.values(deviceLabels).forEach(info => {
				// Ensure label is removed from group *before* element removal if group still exists
				deviceGroup?.remove(info.label);
				info.element.remove();
			});
			deviceLabels = {};
		}
		deviceGroup = null; // Ensure group is nullified
		trackingSpheres = []; // Clear spheres list
	}

	function cleanupHistoryPath() {
		if (historyPathLine) {
			contentGroup?.remove(historyPathLine);
			historyPathLine.geometry?.dispose(); // Dispose geometry
			// Material is shared, dispose carefully elsewhere if needed (or assume it's fine)
			historyPathLine = null;
		}
	}

</script>

<div class="w-full h-full relative" bind:this={container}>
	<!-- Container for the 3D canvas and CSS2D labels -->
</div>

<style>
	/* Add any component-specific styles here if needed */
	/* Ensure container has dimensions */
	div {
		background-color: rgb(30, 41, 59); /* Match Tailwind slate-800 */
	}
	:global(.css2d-renderer-map) {
		/* Ensure label renderer doesn't block interactions with canvas */
		pointer-events: none;
	}
</style>