<script lang="ts" module>
	export type ViewPreset = 'iso' | 'top';
</script>

<script lang="ts">
	// Component to render the 3D map scene
	import { onMount, untrack } from 'svelte';
	import * as THREE from 'three';
	import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
	import { CSS2DRenderer, CSS2DObject } from 'three/examples/jsm/renderers/CSS2DRenderer.js';
	import { SVGLoader } from 'three/examples/jsm/loaders/SVGLoader.js';
	import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
	import type { Device, Node, Config, DeviceHistory, Floor } from '#lib/types.js';
	import { gotoDetail3d } from '#lib/urls.js';
	import { hexToThreeNumber, getRoomColor } from '#lib/colors.js';
	import { wallSegments, wallPosts, wallHeight, MIN_WALL_THICKNESS } from '#lib/walls3d.js';
	import logoSvg from '#lib/images/logo.svg?raw';

	interface Props {
		devicesToShow?: Device[];
		nodesToShow?: Node[];
		config?: Config | null;
		historyData?: DeviceHistory[];
		/** The floor in focus: drawn in full, with labels. Null means the first floor. */
		floorId?: string | null;
		/** Keep the other floors as faint outlines instead of hiding them. */
		showAllFloors?: boolean;
		showNodes?: boolean;
		showDevices?: boolean;
		showHistoryPath?: boolean;
		/** Slow orbit around the plan. Switches itself off when the user drags. */
		autoRotate?: boolean;
	}

	let { devicesToShow = [], nodesToShow = [], config = null, historyData = [], floorId = null, showAllFloors = false, showNodes = true, showDevices = true, showHistoryPath = false, autoRotate = $bindable(false) }: Props = $props();

	let activeFloorId = $derived(floorId ?? config?.floors?.[0]?.id ?? null);

	// --- Internal State ---
	let container: HTMLDivElement = $state()!;
	let ready = $state(false); // flipped once the scene exists so the prop effects can run
	let scene: THREE.Scene;
	let camera: THREE.PerspectiveCamera;
	let renderer: THREE.WebGLRenderer;
	let labelRenderer: CSS2DRenderer;
	let controls: OrbitControls;
	let contentGroup: THREE.Group; // plan coordinates, mirrored to match the 2D map
	let sun: THREE.DirectionalLight;
	let fog: THREE.Fog;
	let planRadius = 10; // metres from the plan's centre to its farthest corner
	let isAnimating = false;
	let animationFrameId: number;
	let lastTime: number | null = null;
	let planCenter = new THREE.Vector3(0, 0, 0);
	let builtConfig: Config | null = null; // the config the rooms on screen were built from
	let raycaster = new THREE.Raycaster();
	let mouse = new THREE.Vector2();
	let pointerDown: { x: number; y: number } | null = null;

	// --- Look ---
	const DEFAULT_WALL_OPACITY = 0.35; // what the 2D map uses when map.wall_opacity is not set
	const GHOST_WALL_FACTOR = 0.2; // walls of a faint floor, as a share of their configured opacity
	const SLAB_THICKNESS = 0.08; // metres of floor under each storey
	const SLAB_OPACITY = { active: 0.85, ghost: 0.06 };
	const FLOOR_OPACITY = { active: 0.2, ghost: 0.05 };
	const GHOST_OPACITY = 0.25;
	const EASE_TAU = 0.2; // seconds: how quickly a device slides to a new fix
	const TWEEN_MS = 700; // camera moves between floors and presets
	const BACKGROUND = 0x1e293b; // slate-800
	// Where the sun stands, seen from the plan: front-left and high, so walls shade one side and
	// throw a short shadow into their rooms.
	const SUN_DIRECTION = new THREE.Vector3(-0.35, -0.5, 1).normalize();
	const CONTROLS_MIN_DISTANCE = 1;
	const CONTROLS_MAX_DISTANCE = 40;
	// Iso looks in from the front-left corner at about 37 degrees elevation; top is straight down
	// with a hair of tilt so the camera's up vector stays defined.
	const VIEW_DIRECTIONS: Record<ViewPreset, THREE.Vector3> = {
		iso: new THREE.Vector3(-0.45, -0.65, 0.6).normalize(),
		top: new THREE.Vector3(0, -0.001, 1).normalize()
	};

	/** Whether something on the given floors is in focus, faint, or not drawn at all. */
	type Presence = 'active' | 'ghost' | 'hidden';
	function presence(onFloors: string[]): Presence {
		if (!activeFloorId || onFloors.length === 0 || onFloors.includes(activeFloorId)) return 'active';
		return showAllFloors ? 'ghost' : 'hidden';
	}

	function deviceFloorIds(device: Device): string[] {
		return device.floor?.id ? [device.floor.id] : [];
	}

	function nodeFloorIds(node: Node): string[] {
		return node.floors ?? [];
	}

	/** Base height of the floor a thing stands on: by floor id, else by its z, else the lowest floor. */
	function floorBaseZ(ids: string[], z: number): number {
		const floors = config?.floors ?? [];
		const byId = floors.find((f) => ids.includes(f.id));
		if (byId) return byId.bounds[0][2];
		const byZ = floors.find((f) => z >= f.bounds[0][2] && z <= f.bounds[1][2]);
		if (byZ) return byZ.bounds[0][2];
		return floors.length ? Math.min(...floors.map((f) => f.bounds[0][2])) : 0;
	}

	function getFlipScale() {
		return { x: config?.map?.flipX ? -1 : 1, y: config?.map?.flipY === false ? -1 : 1 };
	}

	/** Plan coordinates to world coordinates: the plan is mirrored about its centre to match the 2D map. */
	function toWorld(p: THREE.Vector3): THREE.Vector3 {
		const flip = getFlipScale();
		return new THREE.Vector3(planCenter.x + flip.x * (p.x - planCenter.x), planCenter.y + flip.y * (p.y - planCenter.y), p.z);
	}

	// --- Shared geometry ---
	const unitSphere = new THREE.SphereGeometry(1, 24, 16);
	const unitDisc = new THREE.CircleGeometry(1, 24);

	function makeDropLine(material: THREE.LineBasicMaterial): THREE.Line {
		const geometry = new THREE.BufferGeometry();
		geometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0, 0, 0, 0], 3));
		const line = new THREE.Line(geometry, material);
		line.frustumCulled = false; // the bounding sphere is never recomputed as the height changes
		return line;
	}

	function setDropHeight(line: THREE.Line, height: number) {
		const attr = line.geometry.getAttribute('position') as THREE.BufferAttribute;
		attr.setZ(1, height);
		attr.needsUpdate = true;
	}

	// --- Floors ---
	type FloorVisual = {
		floor: Floor;
		group: THREE.Group;
		wallMaterials: THREE.MeshStandardMaterial[];
		wallMeshes: THREE.Mesh[];
		wallOpacity: number; // map.wall_opacity, what the walls have when the floor is in focus
		slab: THREE.Mesh | null;
		planes: THREE.Mesh[];
		labels: CSS2DObject[];
		bounds: THREE.Box3; // plan coordinates
	};
	// eslint-disable-next-line svelte/prefer-svelte-reactivity -- Three.js bookkeeping, never rendered from
	const floorVisuals = new Map<string, FloorVisual>();
	let roomGroup: THREE.Group | null = null;
	let groundGrid: THREE.GridHelper | null = null;

	// --- Devices ---
	type DeviceVisual = {
		group: THREE.Group; // at (x, y, floor base), eased
		sphere: THREE.Mesh;
		line: THREE.Line;
		disc: THREE.Mesh;
		label: CSS2DObject;
		labelElement: HTMLDivElement;
		current: THREE.Vector3; // plan coordinates, eased towards target
		target: THREE.Vector3;
		floorZ: number;
	};
	// eslint-disable-next-line svelte/prefer-svelte-reactivity -- Three.js bookkeeping, never rendered from
	const deviceVisuals = new Map<string, DeviceVisual>();
	let deviceGroup: THREE.Group | null = null;

	type DeviceMaterials = { sphere: THREE.MeshStandardMaterial; ghost: THREE.MeshStandardMaterial; line: THREE.LineBasicMaterial; disc: THREE.MeshBasicMaterial };
	// eslint-disable-next-line svelte/prefer-svelte-reactivity -- plain cache of Three.js materials, never rendered from
	const deviceMaterialCache = new Map<string, DeviceMaterials>();

	function deviceMaterials(device: Device): DeviceMaterials {
		const roomId = device.room?.id;
		const key = roomId || 'default';
		let set = deviceMaterialCache.get(key);
		if (set) return set;
		const color = roomId ? hexToThreeNumber(getRoomColor(config, roomId)) : 0xff4444;
		set = {
			sphere: new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: 0.2, metalness: 0.3, roughness: 0.4 }),
			ghost: new THREE.MeshStandardMaterial({ color, transparent: true, opacity: GHOST_OPACITY, depthWrite: false }),
			line: new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.5 }),
			disc: new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.35, depthWrite: false, side: THREE.DoubleSide })
		};
		deviceMaterialCache.set(key, set);
		return set;
	}

	function clearDeviceMaterials() {
		for (const set of deviceMaterialCache.values()) Object.values(set).forEach((m) => m.dispose());
		deviceMaterialCache.clear();
	}

	/** Long ids as names would sprawl across the plan; the tooltip has the whole thing. */
	function shortName(name: string): string {
		return name.length > 24 ? name.slice(0, 23) + '…' : name;
	}

	function deviceRadius(confidence: number): number {
		// 0.1 m at no confidence up to 0.3 m at 100%
		return 0.1 + 0.2 * (Math.max(0, Math.min(100, confidence)) / 100);
	}

	// --- Nodes ---
	type NodeVisual = {
		group: THREE.Group;
		mesh: THREE.Mesh;
		pick: THREE.Mesh; // invisible, easier to hover than the logo
		line: THREE.Line;
		disc: THREE.Mesh;
		label: CSS2DObject;
		labelElement: HTMLDivElement;
	};
	// eslint-disable-next-line svelte/prefer-svelte-reactivity -- Three.js bookkeeping, never rendered from
	const nodeVisuals = new Map<string, NodeVisual>();
	let nodeGroup: THREE.Group | null = null;
	let nodeLogoGeometry: THREE.BufferGeometry | null = null;

	type NodeMaterialSet = { mesh: THREE.Material; ghost: THREE.Material; line: THREE.LineBasicMaterial; disc: THREE.MeshBasicMaterial };
	function nodeMaterialSet(color: number, baseColor: number): NodeMaterialSet {
		return {
			mesh: new THREE.MeshPhongMaterial({ color: baseColor, emissive: color, emissiveIntensity: 1.5, shininess: 100, toneMapped: false, side: THREE.DoubleSide }),
			ghost: new THREE.MeshBasicMaterial({ color, transparent: true, opacity: GHOST_OPACITY, depthWrite: false, side: THREE.DoubleSide }),
			line: new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.4 }),
			disc: new THREE.MeshBasicMaterial({ color, transparent: true, opacity: 0.3, depthWrite: false, side: THREE.DoubleSide })
		};
	}
	const nodeMaterials = { online: nodeMaterialSet(0x00aaff, 0x001122), offline: nodeMaterialSet(0xff4444, 0x220011) };
	const nodePickMaterial = new THREE.MeshBasicMaterial({ transparent: true, opacity: 0, depthWrite: false });

	function getNodeLogoGeometry(): THREE.BufferGeometry {
		if (nodeLogoGeometry) return nodeLogoGeometry;
		const loader = new SVGLoader();
		const svgData = loader.parse(logoSvg);
		const extrudeSettings = { depth: 20, bevelEnabled: false };
		const geometries: THREE.BufferGeometry[] = [];
		svgData.paths.forEach((path) => {
			// Skip white background circle
			const style = path.userData?.style as { fill?: unknown } | undefined;
			const fill = style?.fill;
			if (fill && typeof fill === 'string' && fill.toLowerCase() === '#ffffff') return;
			const shapes = path.toShapes();
			shapes.forEach((shape) => {
				geometries.push(new THREE.ExtrudeGeometry(shape, extrudeSettings));
			});
		});
		nodeLogoGeometry = mergeGeometries(geometries);
		nodeLogoGeometry.center();
		const scale = 0.0008;
		nodeLogoGeometry.scale(scale, scale, scale);
		return nodeLogoGeometry;
	}

	// --- Measured spheres (click a device) ---
	let measuredSpheresGroup: THREE.Group | null = null;
	let selectedDeviceId: string | null = null;

	function showMeasuredSpheres(device: Device) {
		if (!measuredSpheresGroup) {
			measuredSpheresGroup = new THREE.Group();
			measuredSpheresGroup.name = 'MeasuredSpheres';
			// Nodes live in contentGroup (centred + mirrored); the spheres must too.
			contentGroup?.add(measuredSpheresGroup);
		}
		measuredSpheresGroup.clear();

		Object.entries(device.nodes || {}).forEach(([nodeId, measurement]) => {
			const node = nodesToShow?.find((n) => n.id === nodeId);
			if (!node || !node.location) return;
			if (presence(nodeFloorIds(node)) === 'hidden') return;
			const distance = measurement.dist;
			if (distance <= 0) return;

			const sphereGeometry = new THREE.SphereGeometry(distance, 32, 24);
			const sphereMaterial = new THREE.MeshBasicMaterial({ color: 0x00ffff, wireframe: true, opacity: 0.5, transparent: true });
			const measureSphere = new THREE.Mesh(sphereGeometry, sphereMaterial);
			measureSphere.position.set(node.location.x, node.location.y, node.location.z);
			measuredSpheresGroup?.add(measureSphere);
		});
	}

	function hideMeasuredSpheres() {
		measuredSpheresGroup?.clear();
		selectedDeviceId = null;
	}

	// --- History path ---
	const historyPathMaterial = new THREE.LineBasicMaterial({ color: 0xffffff });
	let historyPathLine: THREE.Line | null = null;

	// --- Hover tooltip ---
	let hover: { title: string; lines: string[]; x: number; y: number } | null = $state(null);

	onMount(() => {
		getNodeLogoGeometry(); // Parse the SVG once, before the first node shows up
		if (container) {
			initScene();
			isAnimating = true;
			animate(performance.now());

			container.addEventListener('pointerdown', onPointerDown);
			container.addEventListener('click', onCanvasClick);
			container.addEventListener('pointermove', onPointerMove);
			container.addEventListener('pointerleave', onPointerLeave);
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
			container?.removeEventListener('pointerdown', onPointerDown);
			container?.removeEventListener('click', onCanvasClick);
			container?.removeEventListener('pointermove', onPointerMove);
			container?.removeEventListener('pointerleave', onPointerLeave);
			cleanupScene();
		};
	});

	// --- Scene Initialization ---
	function initScene() {
		renderer = new THREE.WebGLRenderer({
			antialias: true,
			powerPreference: 'high-performance',
			stencil: false,
			depth: true,
			preserveDrawingBuffer: false
		});
		renderer.setPixelRatio(window.devicePixelRatio);
		renderer.setSize(container.clientWidth, container.clientHeight);
		renderer.setClearColor(BACKGROUND, 1);
		renderer.toneMapping = THREE.ACESFilmicToneMapping;
		renderer.toneMappingExposure = 1.0;
		renderer.autoClear = true;
		renderer.shadowMap.enabled = true;
		renderer.shadowMap.type = THREE.PCFShadowMap;
		// eslint-disable-next-line svelte/no-dom-manipulating -- Three.js owns its canvas; Svelte never renders it
		container.appendChild(renderer.domElement);

		labelRenderer = new CSS2DRenderer();
		labelRenderer.setSize(container.clientWidth, container.clientHeight);
		labelRenderer.domElement.style.position = 'absolute';
		labelRenderer.domElement.style.top = '0px';
		labelRenderer.domElement.style.pointerEvents = 'none';
		labelRenderer.domElement.style.zIndex = '1';
		labelRenderer.domElement.classList.add('css2d-renderer-map');
		// eslint-disable-next-line svelte/no-dom-manipulating -- CSS2DRenderer owns this element too
		container.appendChild(labelRenderer.domElement);

		scene = new THREE.Scene();
		scene.background = new THREE.Color(BACKGROUND);

		camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 1000);
		camera.up.set(0, 0, 1); // z is height: dragging sideways orbits around the vertical, not over the top
		scene.add(camera);

		// Sky from above, a dim bounce from below, and one sun for shape and shadows. fitSun() aims
		// the sun and sizes its shadow to the plan once the rooms are built.
		scene.add(new THREE.HemisphereLight(0xdbeafe, 0x475569, 1.7));
		sun = new THREE.DirectionalLight(0xfff4e5, 1.1);
		sun.castShadow = true;
		sun.shadow.mapSize.set(2048, 2048);
		sun.shadow.bias = -0.0005;
		sun.shadow.normalBias = 0.02;
		sun.shadow.radius = 4; // soft edges
		sun.shadow.intensity = 0.6; // shade, not black: labels and drop discs sit in it
		scene.add(sun);
		scene.add(sun.target);

		// A little fog so the far side of the plan and the edge of the grid fall back; animate() keeps
		// it starting at whatever the camera is looking at.
		fog = new THREE.Fog(BACKGROUND, 10, 100);
		scene.fog = fog;

		controls = new OrbitControls(camera, renderer.domElement);
		controls.enableDamping = true;
		controls.dampingFactor = 0.05;
		controls.minDistance = CONTROLS_MIN_DISTANCE;
		controls.maxDistance = CONTROLS_MAX_DISTANCE;
		controls.maxPolarAngle = Math.PI / 2 - 0.05; // never look up from under the floor
		controls.enablePan = true;
		controls.autoRotate = autoRotate;
		controls.autoRotateSpeed = 0.5; // two minutes per orbit
		controls.addEventListener('start', () => {
			cameraTween = null; // the user takes over from any camera move in progress
			if (autoRotate) autoRotate = false;
		});

		contentGroup = new THREE.Group();
		scene.add(contentGroup);

		setupRooms();
		frameView('iso', false);
		updateSceneObjects();
		ready = true;
	}

	// --- Camera ---
	let cameraTween: { start: number; duration: number; fromPos: THREE.Vector3; toPos: THREE.Vector3; fromTarget: THREE.Vector3; toTarget: THREE.Vector3 } | null = null;

	function easeInOutCubic(s: number): number {
		return s < 0.5 ? 4 * s * s * s : 1 - Math.pow(-2 * s + 2, 3) / 2;
	}

	function moveCamera(toPos: THREE.Vector3, toTarget: THREE.Vector3, animate: boolean) {
		hover = null; // whatever was under the pointer is about to move away
		if (!animate) {
			cameraTween = null;
			camera.position.copy(toPos);
			controls.target.copy(toTarget);
			controls.update();
			return;
		}
		cameraTween = { start: performance.now(), duration: TWEEN_MS, fromPos: camera.position.clone(), toPos: toPos.clone(), fromTarget: controls.target.clone(), toTarget: toTarget.clone() };
	}

	/** World-space box around the floor in focus; the faint floors are context and may run off the edge. */
	function framingBox(): THREE.Box3 {
		const box = new THREE.Box3();
		const focused = activeFloorId ? floorVisuals.get(activeFloorId) : undefined;
		for (const fv of focused ? [focused] : floorVisuals.values()) {
			if (fv.bounds.isEmpty()) continue;
			box.expandByPoint(toWorld(fv.bounds.min));
			box.expandByPoint(toWorld(fv.bounds.max));
		}
		if (box.isEmpty()) box.setFromCenterAndSize(planCenter, new THREE.Vector3(10, 10, 3));
		return box;
	}

	/**
	 * Camera pose looking along `direction` at `box`, pulled back and re-aimed until the box fills the view.
	 * Looking at the box centre leaves the top half empty from an oblique angle, since the near edge projects
	 * much larger than the far one; a few passes of centring then scaling converge on a tight fit.
	 */
	function fitView(box: THREE.Box3, direction: THREE.Vector3): { position: THREE.Vector3; target: THREE.Vector3 } {
		const forward = direction.clone().negate(); // the camera looks from `direction` towards the box
		const up = new THREE.Vector3(0, 0, 1).addScaledVector(forward, -forward.z).normalize(); // world up, squared to the view
		const right = new THREE.Vector3().crossVectors(forward, up).normalize();
		const tanV = Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / 1.1; // leave a margin
		const tanH = tanV * Math.max(camera.aspect, 0.1);
		const corners = Array.from({ length: 8 }, (_, i) => new THREE.Vector3(i & 1 ? box.max.x : box.min.x, i & 2 ? box.max.y : box.min.y, i & 4 ? box.max.z : box.min.z));
		const target = box.getCenter(new THREE.Vector3());
		let distance = Math.max(box.getSize(new THREE.Vector3()).length(), 1);
		const rel = new THREE.Vector3();
		for (let pass = 0; pass < 4; pass++) {
			const position = target.clone().addScaledVector(direction, distance);
			let minX = Infinity;
			let maxX = -Infinity;
			let minY = Infinity;
			let maxY = -Infinity;
			for (const corner of corners) {
				rel.subVectors(corner, position);
				const depth = Math.max(rel.dot(forward), 0.01);
				const x = rel.dot(right) / depth;
				const y = rel.dot(up) / depth;
				minX = Math.min(minX, x);
				maxX = Math.max(maxX, x);
				minY = Math.min(minY, y);
				maxY = Math.max(maxY, y);
			}
			// Re-aim at the middle of what is on screen, then move in or out so the wider side just fits
			target.addScaledVector(right, ((minX + maxX) / 2) * distance).addScaledVector(up, ((minY + maxY) / 2) * distance);
			distance *= Math.max((maxX - minX) / 2 / tanH, (maxY - minY) / 2 / tanV);
		}
		return { position: target.clone().addScaledVector(direction, distance), target };
	}

	/** Move the camera to a preset view of the floors in view. */
	export function frameView(preset: ViewPreset, animate = true) {
		if (!camera || !controls) return;
		const { position, target } = fitView(framingBox(), VIEW_DIRECTIONS[preset]);
		controls.maxDistance = Math.max(CONTROLS_MAX_DISTANCE, position.distanceTo(target) * 3);
		moveCamera(position, target, animate);
	}

	/** Re-centre on the floor in focus, keeping the user's angle and zoom. */
	function focusFloor() {
		if (!camera || !controls) return;
		const offset = camera.position.clone().sub(controls.target);
		const { target } = fitView(framingBox(), offset.clone().normalize());
		moveCamera(target.clone().add(offset), target, true);
	}

	// --- Scene Updates ---
	function updateSceneObjects() {
		if (!contentGroup) return;

		if (showDevices) renderDevices(devicesToShow);
		else cleanupDeviceGroup();

		if (showNodes) renderNodes(nodesToShow);
		else cleanupNodeGroup();

		if (showHistoryPath) renderHistoryPath(historyData);
		else cleanupHistoryPath();
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
		labelDivEle.className = 'text-white/80 text-sm text-center pointer-events-none';
		labelDivEle.textContent = name;
		const labelElement = new CSS2DObject(labelDivEle);
		labelElement.name = 'roomLabel';
		labelElement.position.set(center.x, center.y, 0);
		return labelElement;
	}

	function setupRooms() {
		if (!config?.floors || !contentGroup) return;

		builtConfig = config;
		cleanupRooms();
		clearDeviceMaterials(); // room colours may have changed
		const newRoomGroup = new THREE.Group();
		newRoomGroup.name = 'RoomGroup';
		const overallBounds = new THREE.Box3();

		const wallThickness = Math.max(config.map?.wallThickness ?? 0.1, MIN_WALL_THICKNESS);
		const wallColor = config.map?.wallColor;
		const wallOpacity = Math.max(0, Math.min(1, config.map?.wallOpacity ?? DEFAULT_WALL_OPACITY));

		config.floors.forEach((floor) => {
			const floor_base = floor.bounds[0][2];
			const floor_ceiling = floor.bounds[1][2];
			const floorGroup = new THREE.Group();
			floorGroup.name = 'floor#' + floor.id;
			const floorBounds = new THREE.Box3();
			const planes: THREE.Mesh[] = [];
			const labels: CSS2DObject[] = [];
			const slabGeometries: THREE.BufferGeometry[] = [];

			floor.rooms?.forEach((room) => {
				const pointsFloor: THREE.Vector2[] = [];

				room.points.forEach((points) => {
					floorBounds.expandByPoint(new THREE.Vector3(points[0], points[1], floor_base));
					floorBounds.expandByPoint(new THREE.Vector3(points[0], points[1], floor_ceiling));
					pointsFloor.push(new THREE.Vector2(points[0], points[1]));
				});
				if (pointsFloor.length < 3) return;

				// Floor plane with unique color per room
				const floorShape = new THREE.Shape(pointsFloor);
				const floorGeometry = new THREE.ShapeGeometry(floorShape);
				const floorMaterial = new THREE.MeshStandardMaterial({
					color: hexToThreeNumber(getRoomColor(config, room.id)),
					side: THREE.DoubleSide,
					opacity: FLOOR_OPACITY.active,
					transparent: true,
					depthWrite: false, // the slab underneath holds the depth; the tint must not hide drop discs
					roughness: 1.0,
					metalness: 0.0
				});
				const plane = new THREE.Mesh(floorGeometry, floorMaterial);
				plane.position.z = floor_base;
				plane.renderOrder = -1; // over the slab, under everything standing on the floor
				plane.receiveShadow = true;
				floorGroup.add(plane);
				planes.push(plane);

				// The slab the room stands on, a hair below the tinted plane so the two never z-fight
				const slabGeometry = new THREE.ExtrudeGeometry(floorShape, { depth: SLAB_THICKNESS, bevelEnabled: false });
				slabGeometry.translate(0, 0, floor_base - SLAB_THICKNESS - 0.005);
				slabGeometries.push(slabGeometry);

				const label = createLabelForRoom(room.name, pointsFloor);
				label.position.z = floor_base;
				floorGroup.add(label);
				labels.push(label);
			});

			let slab: THREE.Mesh | null = null;
			if (slabGeometries.length > 0) {
				const slabMaterial = new THREE.MeshStandardMaterial({ color: 0x334155, transparent: true, opacity: SLAB_OPACITY.active, roughness: 1.0, metalness: 0.0 });
				slab = new THREE.Mesh(mergeGeometries(slabGeometries), slabMaterial);
				slab.renderOrder = -2;
				slab.receiveShadow = true;
				slab.castShadow = true; // shades the floors below
				floorGroup.add(slab);
				slabGeometries.forEach((g) => g.dispose());
			}

			// Walls: a slab per room edge, cut low so the camera sees in. One mesh per colour: with
			// map.wall_color that is one for the floor, without it each wall has its room's colour.
			const height = wallHeight(floor_base, floor_ceiling);
			const wallMaterials: THREE.MeshStandardMaterial[] = [];
			const wallMeshes: THREE.Mesh[] = [];
			if (height > 0) {
				// eslint-disable-next-line svelte/prefer-svelte-reactivity -- function-local temp
				const byColor = new Map<string, THREE.BufferGeometry[]>();
				const add = (roomId: string, box: THREE.BufferGeometry) => {
					const color = wallColor ?? getRoomColor(config, roomId);
					const list = byColor.get(color);
					if (list) list.push(box);
					else byColor.set(color, [box]);
				};
				const segments = wallSegments(floor.rooms ?? []);
				for (const wall of segments) {
					const dx = wall.b[0] - wall.a[0];
					const dy = wall.b[1] - wall.a[1];
					// Each wall stops half a thickness short of its ends; the posts below fill the corners
					const length = Math.hypot(dx, dy) - wallThickness;
					if (length <= 0) continue;
					const box = new THREE.BoxGeometry(length, wallThickness, height);
					box.rotateZ(Math.atan2(dy, dx));
					box.translate((wall.a[0] + wall.b[0]) / 2, (wall.a[1] + wall.b[1]) / 2, floor_base + height / 2);
					add(wall.roomId, box);
				}
				for (const post of wallPosts(segments)) {
					const box = new THREE.BoxGeometry(wallThickness, wallThickness, height);
					box.translate(post.at[0], post.at[1], floor_base + height / 2);
					add(post.roomId, box);
				}
				for (const [color, boxes] of byColor) {
					const tint = new THREE.Color(color);
					const material = new THREE.MeshStandardMaterial({ color: tint, transparent: true, opacity: wallOpacity, depthWrite: wallOpacity >= 1, roughness: 0.9, metalness: 0.0 });
					const wallMesh = new THREE.Mesh(mergeGeometries(boxes), material);
					wallMesh.castShadow = wallMesh.receiveShadow = true;
					wallMeshes.push(wallMesh);
					floorGroup.add(wallMesh);
					wallMaterials.push(material);
					boxes.forEach((g) => g.dispose());
				}
			}

			if (floorBounds.isEmpty() && floor.bounds.length >= 2) {
				const [lo, hi] = floor.bounds;
				floorBounds.set(new THREE.Vector3(lo[0], lo[1], lo[2]), new THREE.Vector3(hi[0], hi[1], hi[2]));
			}
			overallBounds.union(floorBounds);
			newRoomGroup.add(floorGroup);
			floorVisuals.set(floor.id, { floor, group: floorGroup, wallMaterials, wallMeshes, wallOpacity, slab, planes, labels, bounds: floorBounds });
		});

		// A 1 m grid under the house for a sense of scale, on whole metres of the plan
		if (!overallBounds.isEmpty()) {
			const size = overallBounds.getSize(new THREE.Vector3());
			const center = overallBounds.getCenter(new THREE.Vector3());
			const cells = 2 * Math.ceil(Math.max(size.x, size.y) / 2 + 2);
			groundGrid = new THREE.GridHelper(cells, cells, 0x64748b, 0x475569);
			groundGrid.rotation.x = Math.PI / 2; // GridHelper lies in XZ; here z is up
			groundGrid.position.set(Math.round(center.x), Math.round(center.y), 0);
			const gridMaterial = groundGrid.material as THREE.LineBasicMaterial;
			gridMaterial.transparent = true;
			gridMaterial.opacity = 0.35;
			gridMaterial.depthWrite = false;
			groundGrid.renderOrder = -3;
			newRoomGroup.add(groundGrid);
		}

		contentGroup.add(newRoomGroup);
		roomGroup = newRoomGroup;

		if (!overallBounds.isEmpty()) {
			overallBounds.getCenter(planCenter);
		} else {
			planCenter.set(0, 0, 0);
		}
		// Mirror the plan to match the 2D map: there flip_x puts x=0 on the right
		// and flip_y (the default) puts y=0 at the bottom, as the camera already does.
		const flip = getFlipScale();
		contentGroup.scale.set(flip.x, flip.y, 1);
		contentGroup.position.set(planCenter.x - flip.x * planCenter.x, planCenter.y - flip.y * planCenter.y, 0);
		for (const v of nodeVisuals.values()) v.mesh.scale.set(flip.x, flip.y, 1);

		fitSun(overallBounds);
		applyFloorPresence();
	}

	/** Aim the sun at the plan and size its shadow to cover every floor, however large the house is. */
	function fitSun(bounds: THREE.Box3) {
		const sphere = new THREE.Sphere(planCenter.clone(), 10);
		if (!bounds.isEmpty()) bounds.getBoundingSphere(sphere); // mirroring about the centre keeps the same sphere
		planRadius = Math.max(sphere.radius, 1);
		if (!sun) return;
		const reach = planRadius + 1;
		sun.target.position.copy(sphere.center);
		sun.position.copy(sphere.center).addScaledVector(SUN_DIRECTION, reach * 2);
		const shadowCamera = sun.shadow.camera;
		shadowCamera.left = shadowCamera.bottom = -reach;
		shadowCamera.right = shadowCamera.top = reach;
		shadowCamera.near = 0.1;
		shadowCamera.far = reach * 4;
		shadowCamera.updateProjectionMatrix();
		sun.shadow.needsUpdate = true;
	}

	function applyFloorPresence() {
		let lowestShown = Infinity;
		for (const fv of floorVisuals.values()) {
			const p = presence([fv.floor.id]);
			fv.group.visible = p !== 'hidden';
			if (p === 'hidden') continue;
			lowestShown = Math.min(lowestShown, fv.floor.bounds[0][2]);
			for (const material of fv.wallMaterials) {
				material.opacity = p === 'active' ? fv.wallOpacity : fv.wallOpacity * GHOST_WALL_FACTOR;
				material.depthWrite = p === 'active' && fv.wallOpacity >= 1;
			}
			if (fv.slab) {
				const slabMaterial = fv.slab.material as THREE.MeshStandardMaterial;
				slabMaterial.opacity = SLAB_OPACITY[p];
				slabMaterial.depthWrite = p === 'active'; // a faint floor must not hide what is under it
				fv.slab.castShadow = p === 'active'; // nor darken the one in focus
			}
			for (const mesh of fv.wallMeshes) mesh.castShadow = p === 'active';
			for (const plane of fv.planes) {
				(plane.material as THREE.MeshStandardMaterial).opacity = FLOOR_OPACITY[p];
			}
			for (const label of fv.labels) label.visible = p === 'active';
		}
		// The grid lies under the lowest floor on screen, not metres below a lone upper floor
		if (groundGrid) {
			groundGrid.visible = Number.isFinite(lowestShown);
			groundGrid.position.z = (Number.isFinite(lowestShown) ? lowestShown : 0) - SLAB_THICKNESS - 0.02;
		}
	}

	// --- Node Rendering ---
	function renderNodes(nodes: Node[]) {
		if (!contentGroup) return;

		if (!nodeGroup) {
			nodeGroup = new THREE.Group();
			nodeGroup.name = 'NodeGroup';
			contentGroup.add(nodeGroup);
		}

		// eslint-disable-next-line svelte/prefer-svelte-reactivity -- function-local temp
		const currentNodeIds = new Set<string>();
		const flip = getFlipScale();

		nodes.forEach((node) => {
			if (!node.location) {
				console.warn('Node missing location:', node);
				return;
			}
			currentNodeIds.add(node.id);

			let v = nodeVisuals.get(node.id);
			if (!v) {
				v = createNodeVisual(node);
				nodeVisuals.set(node.id, v);
				nodeGroup?.add(v.group);
			}

			const floorZ = floorBaseZ(nodeFloorIds(node), node.location.z);
			const height = node.location.z - floorZ;
			v.group.position.set(node.location.x, node.location.y, floorZ);
			v.mesh.position.z = height;
			v.mesh.scale.set(flip.x, flip.y, 1); // undo the plan mirroring so the logo isn't drawn mirror-image
			v.pick.position.z = height;
			v.label.position.z = height;
			setDropHeight(v.line, height);

			const mats = node.online ? nodeMaterials.online : nodeMaterials.offline;
			const p = presence(nodeFloorIds(node));
			v.group.visible = p !== 'hidden';
			v.mesh.material = p === 'ghost' ? mats.ghost : mats.mesh;
			v.line.material = mats.line;
			v.disc.material = mats.disc;
			v.line.visible = v.disc.visible = v.label.visible = p === 'active';
			v.labelElement.textContent = node.name;
			v.labelElement.className = `text-xs pointer-events-none ${node.online ? 'text-blue-400' : 'text-red-500'}`;
			v.pick.userData = { kind: 'node', id: node.id };
		});

		for (const [nodeId, v] of nodeVisuals) {
			if (currentNodeIds.has(nodeId)) continue;
			removeNodeVisual(v);
			nodeVisuals.delete(nodeId);
		}
	}

	function createNodeVisual(node: Node): NodeVisual {
		const group = new THREE.Group();
		group.name = 'node#' + node.id;

		const mesh = new THREE.Mesh(getNodeLogoGeometry().clone(), nodeMaterials.online.mesh);
		mesh.castShadow = true;
		mesh.receiveShadow = true;
		group.add(mesh);

		const pick = new THREE.Mesh(unitSphere, nodePickMaterial);
		pick.scale.setScalar(0.25);
		group.add(pick);

		const line = makeDropLine(nodeMaterials.online.line);
		group.add(line);

		const disc = new THREE.Mesh(unitDisc, nodeMaterials.online.disc);
		disc.scale.setScalar(0.1);
		disc.position.z = 0.01;
		group.add(disc);

		const labelElement = document.createElement('div');
		labelElement.style.marginTop = '-1em'; // Position above the node
		const label = new CSS2DObject(labelElement);
		label.name = 'nodeLabel#' + node.id;
		group.add(label);

		return { group, mesh, pick, line, disc, label, labelElement };
	}

	function removeNodeVisual(v: NodeVisual) {
		v.labelElement.remove();
		v.group.removeFromParent();
		v.line.geometry.dispose();
		v.mesh.geometry.dispose();
	}

	// --- Device Rendering ---
	function renderDevices(devices: Device[]) {
		if (!contentGroup) return;

		if (!deviceGroup) {
			deviceGroup = new THREE.Group();
			deviceGroup.name = 'DeviceGroup';
			contentGroup.add(deviceGroup);
		}

		// eslint-disable-next-line svelte/prefer-svelte-reactivity -- function-local temp
		const currentDeviceIds = new Set<string>();

		devices.forEach((device) => {
			if (!device.location || (device.confidence || 0) <= 1) return; // Skip if no location or low confidence
			currentDeviceIds.add(device.id);

			const mats = deviceMaterials(device);
			let v = deviceVisuals.get(device.id);
			if (!v) {
				v = createDeviceVisual(device, mats);
				v.current.set(device.location.x, device.location.y, device.location.z); // appear in place, don't fly in
				deviceVisuals.set(device.id, v);
				deviceGroup?.add(v.group);
			}

			v.target.set(device.location.x, device.location.y, device.location.z);
			v.floorZ = floorBaseZ(deviceFloorIds(device), device.location.z);
			v.sphere.scale.setScalar(deviceRadius(device.confidence || 0));

			const p = presence(deviceFloorIds(device));
			v.group.visible = p !== 'hidden';
			v.sphere.material = p === 'ghost' ? mats.ghost : mats.sphere;
			v.line.material = mats.line;
			v.disc.material = mats.disc;
			v.line.visible = v.disc.visible = v.label.visible = p === 'active';
			v.labelElement.textContent = shortName(device.name || device.id);
			v.sphere.userData = { kind: 'device', id: device.id };
			placeDevice(v);
		});

		for (const [deviceId, v] of deviceVisuals) {
			if (currentDeviceIds.has(deviceId)) continue;
			removeDeviceVisual(v);
			deviceVisuals.delete(deviceId);
		}
	}

	function createDeviceVisual(device: Device, mats: DeviceMaterials): DeviceVisual {
		const group = new THREE.Group();
		group.name = 'device#' + device.id;

		const sphere = new THREE.Mesh(unitSphere, mats.sphere);
		sphere.castShadow = true;
		sphere.receiveShadow = true;
		group.add(sphere);

		const line = makeDropLine(mats.line);
		group.add(line);

		const disc = new THREE.Mesh(unitDisc, mats.disc);
		disc.scale.setScalar(0.12);
		disc.position.z = 0.01;
		group.add(disc);

		const labelElement = document.createElement('div');
		labelElement.className = 'text-white text-xs pointer-events-none';
		labelElement.style.marginTop = '-1em';
		const label = new CSS2DObject(labelElement);
		label.name = device.id + '#label';
		group.add(label);

		return { group, sphere, line, disc, label, labelElement, current: new THREE.Vector3(), target: new THREE.Vector3(), floorZ: 0 };
	}

	function removeDeviceVisual(v: DeviceVisual) {
		v.labelElement.remove();
		v.group.removeFromParent();
		v.line.geometry.dispose();
	}

	/** Put a device where its eased position says, with the drop line reaching its floor. */
	function placeDevice(v: DeviceVisual) {
		const height = v.current.z - v.floorZ;
		v.group.position.set(v.current.x, v.current.y, v.floorZ);
		v.sphere.position.z = height;
		v.label.position.z = height;
		setDropHeight(v.line, height);
	}

	function easeDevices(deltaTime: number) {
		const k = 1 - Math.exp(-deltaTime / EASE_TAU);
		for (const v of deviceVisuals.values()) {
			if (v.current.distanceToSquared(v.target) < 1e-8) continue;
			v.current.lerp(v.target, k);
			if (v.current.distanceToSquared(v.target) < 1e-8) v.current.copy(v.target);
			placeDevice(v);
		}
	}

	// --- History Path Rendering ---
	function renderHistoryPath(history: DeviceHistory[]) {
		cleanupHistoryPath();
		if (!contentGroup || history.length < 2) return;

		// The backend writes one row per scenario; draw the path of the winning scenario only.
		const best = history.filter((h) => h.best);
		const rows = best.length > 0 ? best : history;
		const points = rows.filter((h) => h.x != null && h.y != null && h.z != null).map((h) => new THREE.Vector3(h.x!, h.y!, h.z!));

		if (points.length > 1) {
			const geometry = new THREE.BufferGeometry().setFromPoints(points);
			historyPathLine = new THREE.Line(geometry, historyPathMaterial);
			historyPathLine.name = 'HistoryPathLine';
			contentGroup.add(historyPathLine);
		}
	}

	// --- Picking ---
	function isShown(object: THREE.Object3D): boolean {
		for (let o: THREE.Object3D | null = object; o; o = o.parent) if (!o.visible) return false;
		return true;
	}

	function pick(event: MouseEvent, kinds: Array<'device' | 'node'>): { kind: 'device' | 'node'; id: string } | null {
		if (!camera || !container) return null;
		const rect = container.getBoundingClientRect();
		mouse.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
		mouse.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
		raycaster.setFromCamera(mouse, camera);

		const candidates: THREE.Object3D[] = [];
		if (kinds.includes('device')) for (const v of deviceVisuals.values()) if (isShown(v.sphere)) candidates.push(v.sphere);
		if (kinds.includes('node')) for (const v of nodeVisuals.values()) if (isShown(v.pick)) candidates.push(v.pick);
		const hit = raycaster.intersectObjects(candidates, false)[0]?.object;
		return hit ? (hit.userData as { kind: 'device' | 'node'; id: string }) : null;
	}

	function describe(hit: { kind: 'device' | 'node'; id: string }): { title: string; lines: string[] } | null {
		if (hit.kind === 'device') {
			const device = devicesToShow.find((d) => d.id === hit.id);
			if (!device) return null;
			const lines: string[] = [];
			if (device.name && device.name !== device.id) lines.push(device.id);
			lines.push([device.room?.name, device.floor?.name].filter(Boolean).join(' · ') || 'No room');
			lines.push(`${device.confidence}% confidence, ${device.fixes} fixes`);
			return { title: device.name || device.id, lines };
		}
		const node = nodesToShow.find((n) => n.id === hit.id);
		if (!node) return null;
		const floors = (node.floors ?? []).map((id) => config?.floors.find((f) => f.id === id)?.name ?? id);
		const lines = [node.online ? 'Online' : 'Offline'];
		if (floors.length) lines.push(floors.join(', '));
		if (node.location) lines.push(`${node.location.x}, ${node.location.y}, ${node.location.z} m`);
		return { title: node.name, lines };
	}

	function onPointerMove(event: PointerEvent) {
		const hit = pick(event, ['device', 'node']);
		const info = hit ? describe(hit) : null;
		container.style.cursor = hit?.kind === 'device' ? 'pointer' : '';
		if (!info) {
			if (hover) hover = null;
			return;
		}
		const rect = container.getBoundingClientRect();
		hover = { ...info, x: event.clientX - rect.left, y: event.clientY - rect.top };
	}

	function onPointerLeave() {
		hover = null;
		container.style.cursor = '';
	}

	function onPointerDown(event: PointerEvent) {
		pointerDown = { x: event.clientX, y: event.clientY };
	}

	// --- Click Handling ---
	function onCanvasClick(event: MouseEvent) {
		// A drag to orbit ends with a click too; only a tap counts
		if (pointerDown && Math.hypot(event.clientX - pointerDown.x, event.clientY - pointerDown.y) > 4) return;

		const hit = pick(event, ['device']);
		if (!hit) {
			hideMeasuredSpheres();
			return;
		}
		if (selectedDeviceId === hit.id) {
			// Clicking the same device again hides the spheres and opens it
			hideMeasuredSpheres();
			gotoDetail3d(hit.id);
		} else {
			const device = devicesToShow.find((d) => d.id === hit.id);
			if (device) {
				selectedDeviceId = hit.id;
				showMeasuredSpheres(device);
			}
		}
	}

	// --- Animation Loop ---
	function animate(currentTime: number) {
		if (!isAnimating) return;

		animationFrameId = requestAnimationFrame(animate);

		if (lastTime === null) lastTime = currentTime;
		const deltaTime = Math.min((currentTime - lastTime) * 0.001, 0.1); // seconds, capped after a background tab
		lastTime = currentTime;

		if (cameraTween) {
			const s = easeInOutCubic(Math.min(1, (currentTime - cameraTween.start) / cameraTween.duration));
			camera.position.lerpVectors(cameraTween.fromPos, cameraTween.toPos, s);
			controls.target.lerpVectors(cameraTween.fromTarget, cameraTween.toTarget, s);
			if (s >= 1) cameraTween = null;
		}
		controls?.update();
		easeDevices(deltaTime);

		// Fog starts just past what the camera is looking at, so zooming never fogs the subject
		const focus = camera.position.distanceTo(controls.target);
		fog.near = focus + planRadius * 0.25;
		fog.far = focus + planRadius * 5;

		renderer?.render(scene, camera);
		labelRenderer?.render(scene, camera);
	}

	// --- Cleanup ---
	function cleanupScene() {
		scene?.traverse((object) => {
			if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
				object.geometry?.dispose();
				if (Array.isArray(object.material)) {
					object.material.forEach((material) => material.dispose());
				} else if (object.material) {
					object.material.dispose();
				}
			}
		});

		cleanupRooms();
		cleanupNodeGroup();
		cleanupDeviceGroup();
		cleanupHistoryPath();
		clearDeviceMaterials();

		renderer?.dispose();
		renderer?.forceContextLoss(); // Important for WebGL context release
		labelRenderer?.domElement?.remove();
	}

	function cleanupRooms() {
		if (roomGroup) {
			roomGroup.traverse((object) => {
				if (object instanceof CSS2DObject) {
					object.element?.remove();
				} else if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
					object.geometry?.dispose();
					(Array.isArray(object.material) ? object.material : [object.material]).forEach((m) => m?.dispose());
				}
			});
			contentGroup?.remove(roomGroup);
			roomGroup = null;
		}
		groundGrid = null;
		floorVisuals.clear();
	}

	function cleanupNodeGroup() {
		for (const v of nodeVisuals.values()) removeNodeVisual(v);
		nodeVisuals.clear();
		if (nodeGroup) contentGroup?.remove(nodeGroup);
		nodeGroup = null;
	}

	function cleanupDeviceGroup() {
		for (const v of deviceVisuals.values()) removeDeviceVisual(v);
		deviceVisuals.clear();
		if (deviceGroup) contentGroup?.remove(deviceGroup);
		deviceGroup = null;
	}

	function cleanupHistoryPath() {
		if (historyPathLine) {
			contentGroup?.remove(historyPathLine);
			historyPathLine.geometry?.dispose();
			historyPathLine = null;
		}
	}

	// --- Lifecycle ---
	// Rebuild the rooms only when the config changes. setupRooms() reads the floor in focus through
	// applyFloorPresence(), so untrack it or every floor-tab click would rebuild every room.
	$effect(() => {
		if (!ready || !config || config === builtConfig) return;
		untrack(() => {
			setupRooms();
			updateSceneObjects(); // room colours may have changed, and the device materials with them
		});
	});
	$effect(() => {
		if (ready) updateSceneObjects();
	});
	$effect(() => {
		if (ready) applyFloorPresence();
	});
	$effect(() => {
		if (ready && controls) controls.autoRotate = autoRotate;
	});
	// Follow the floor in focus with the camera, but not on the first paint: initScene framed that.
	let focusedFloorId: string | null = null;
	$effect(() => {
		const id = activeFloorId;
		if (!ready) return;
		if (focusedFloorId !== null && focusedFloorId !== id) focusFloor();
		focusedFloorId = id;
	});
</script>

<div class="map3d w-full h-full relative" bind:this={container}>
	<!-- Container for the 3D canvas and CSS2D labels -->
	{#if hover}
		<div class="pointer-events-none absolute z-10 rounded bg-surface-100-900/90 px-2 py-1 text-xs shadow" style="left: {hover.x + 14}px; top: {hover.y + 14}px" role="tooltip" data-testid="map3d-tooltip">
			<div class="font-semibold">{hover.title}</div>
			{#each hover.lines as line, i (i)}
				<div class="opacity-80">{line}</div>
			{/each}
		</div>
	{/if}
</div>

<style>
	.map3d {
		background-color: rgb(30, 41, 59); /* Match Tailwind slate-800 */
	}
	:global(.css2d-renderer-map) {
		/* Ensure label renderer doesn't block interactions with canvas */
		pointer-events: none;
	}
</style>
